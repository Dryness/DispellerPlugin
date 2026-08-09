using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
// Lumina's Cabinet sheet and FFXIVClientStructs' Cabinet store are both needed here and share
// a name. The sheet is the one that gets renamed, since the store is the subject of the file.
using Lumina.Excel.Sheets;
using CabinetSheet = Lumina.Excel.Sheets.Cabinet;

namespace Dispeller.Services;

/// <summary>
/// What the character actually has stored in their Armoire, as a set of item ids.
///
/// Deliberately a separate class from <see cref="DresserScanner"/> rather than a second
/// method on it, even though the two are shaped the same: they read different game state,
/// they can be stale independently of one another, and the window has to be able to say
/// which of the two is out of date. Folding them together would mean one staleness flag
/// covering two stores that go stale at different moments.
/// </summary>
public class ArmoireScanner : IDisposable
{
    private static readonly object LockObject = new();
    private static HashSet<uint> _cachedItemIds = [];
    private static long _cachedSignature = -1;
    private static int _generation = 0;
    private static ulong _contentId = 0;
    private static volatile bool _hasData = false;
    private static volatile bool _fromSavedCache = false;
    private static DateTimeOffset _savedAt;

    private const int CacheFormatVersion = 1;

    /// <summary>
    /// False until the Armoire has been read once for this character, live or off disk. This
    /// is not the same as "the Armoire is empty": an empty Armoire is a fact, an unread one is
    /// the absence of one, and the window must not draw the second as though it were the first.
    /// </summary>
    public static bool HasData => _hasData;

    /// <summary>
    /// True when what is cached came off disk and has not been confirmed against the game this
    /// session. A saved copy is only ever a best guess until the Armoire is opened again - the
    /// character may have stored or withdrawn while the plugin was off, or on another machine.
    /// </summary>
    public static bool IsFromSavedCache => _fromSavedCache;

    /// <summary>When the cache on disk was written. Only meaningful while <see cref="IsFromSavedCache"/>.</summary>
    public static DateTimeOffset SavedAt => _savedAt;

    /// <summary>
    /// Bumped every time the stored set actually changes. MainWindow watches this exactly as it
    /// watches <see cref="DresserScanner.Generation"/>.
    /// </summary>
    public static int Generation => Volatile.Read(ref _generation);

    // Polled on the same cadence as the dresser, and for the same reason: the game offers no
    // change signal, and the contents change as the player stores and withdraws. Both of those
    // happen through the Armoire's own window, so polling while it is open is enough to keep up.
    private const int PollIntervalFrames = 30;
    private int _framesSincePoll = PollIntervalFrames;

    // Cabinet sheet row -> item id, built once. The game's IsItemInCabinet is keyed by the
    // sheet's row id, not by item id, so the mapping has to be walked in that direction; over
    // 5000 rows that is not something to redo on every poll.
    //
    // The category rides along only so the log can break the result down by the Armoire's own
    // tabs - "Costumes", "Fashions", "Dungeon Gear" - which is a breakdown that can be checked
    // against the game's own UI by hand, unlike a bare total.
    private static (uint CabinetRow, uint ItemId, uint Category)[]? _cabinetRows;
    private static Dictionary<uint, string>? _categoryNames;

    private bool _disposed = false;

    public ArmoireScanner()
    {
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    private unsafe void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            // Same character watch as the dresser's, for the same reasons: this covers login,
            // logout (id 0) and the plugin being enabled mid-session in one place.
            var contentId = Plugin.ClientState.IsLoggedIn ? Plugin.PlayerState.ContentId : 0;
            if (contentId != _contentId)
                SwitchCharacter(contentId);

            if (contentId == 0)
                return;

            // Read only while a window that deals in Armoire contents is open.
            //
            // IsCabinetLoaded() returns true over a bitfield the client has only partly
            // received; opening one of these windows is what completes it. A partial read is
            // indistinguishable from a complete one after the fact, so the only defence is to
            // not take one.
            var uiState = UIState.Instance();
            var gate = OpenGate();

            if (gate == null || uiState == null || !uiState->Cabinet.IsCabinetLoaded())
            {
                // Arm the next poll, so opening one of them is read on the first frame its data
                // lands rather than up to half a second later.
                _framesSincePoll = PollIntervalFrames;
                return;
            }

            if (++_framesSincePoll < PollIntervalFrames)
                return;
            _framesSincePoll = 0;

            var itemIds = ReadAll(uiState);

            // An empty Armoire is a legitimate answer here, unlike an empty dresser read: the
            // gate above establishes that the data is real, so an empty set means empty rather
            // than not-yet-arrived and there is nothing to throw away.
            var signature = SignatureOf(itemIds);

            // Losing a large share of the set in one poll is not a player emptying their
            // Armoire - withdrawing is one item at a time - it is an incomplete read. Not
            // refused, because refusing would need a rule for when to stop refusing, but said
            // out loud so it cannot go unnoticed the way the first one did.
            var previousCount = _cachedItemIds.Count;
            if (previousCount > 0 && itemIds.Count * 4 < previousCount * 3)
                Plugin.Log.Warning(
                    $"Armoire read via {gate} dropped from {previousCount} to {itemIds.Count} items "
                    + $"(cabinet state {uiState->Cabinet.State}) - suspect an incomplete read");

            // A successful read confirms the cache against the game whether or not anything
            // changed, so the "cached" notice clears even when the saved copy was accurate.
            var wasUnconfirmed = !_hasData || _fromSavedCache;
            _fromSavedCache = false;
            _hasData = true;

            lock (LockObject)
            {
                if (!wasUnconfirmed && signature == _cachedSignature)
                    return;

                _cachedItemIds = itemIds;
                _cachedSignature = signature;
            }

            Interlocked.Increment(ref _generation);
            Save();
            // The gate is named in the log deliberately. Which windows actually complete the
            // bitfield is an empirical question, and if one of them ever caches a short read
            // this line is what says which one did it.
            Plugin.Log.Information(
                $"Armoire cache updated: {itemIds.Count} items stored via {gate}, "
                + $"cabinet state {uiState->Cabinet.State} (generation {Generation})");
            LogBreakdown(itemIds);
        }
        catch
        {
            // Swallowed for the same reason DresserScanner swallows: this runs every frame, so
            // a recurring fault would flood the log. The next poll retries from scratch.
        }
    }

    /// <summary>
    /// The window, if any, that licenses a read this frame - or null to leave the cache alone.
    ///
    /// Three doors lead to Armoire contents and all three have to work, because a player who
    /// withdrew something through one of them would otherwise be looking at results that still
    /// counted it. "Store an item" and "Remove an item" are separate agents with separate
    /// addons, so covering the first does nothing for the second, and the Glamour Dresser
    /// reaches Armoire items without either being involved.
    ///
    /// Named rather than boolean so the log can say which door a given read came through.
    /// </summary>
    private static unsafe string? OpenGate()
    {
        var store = AgentCabinet.Instance();
        if (store != null && store->IsAddonReady())
            return "Armoire (store)";

        var withdraw = AgentCabinetWithdraw.Instance();
        if (withdraw != null && withdraw->IsAddonReady())
            return "Armoire (remove)";

        var dresser = AgentMiragePrismPrismBox.Instance();
        if (dresser != null && dresser->IsAddonReady())
            return "Glamour Dresser";

        return null;
    }

    /// <summary>
    /// Walks the Cabinet sheet and asks the game about each row. The item id is what comes back,
    /// because that is the identity everything else in the plugin is keyed on - the cabinet row
    /// is an implementation detail of the lookup and is not worth carrying any further.
    /// </summary>
    private static unsafe HashSet<uint> ReadAll(UIState* uiState)
    {
        // Rows with no item attached are placeholders in the sheet and are skipped - roughly a
        // quarter of it. They would answer the lookup happily and add item id 0 to the set.
        _cabinetRows ??= Plugin.DataManager.GetExcelSheet<CabinetSheet>()!
            .Where(row => row.Item.RowId != 0)
            .Select(row => (row.RowId, row.Item.RowId, row.Category.RowId))
            .ToArray();

        var stored = new HashSet<uint>();

        foreach (var (cabinetRow, itemId, _) in _cabinetRows)
        {
            if (uiState->Cabinet.IsItemInCabinet(cabinetRow))
                stored.Add(itemId);
        }

        return stored;
    }

    /// <summary>
    /// The result split by the Armoire's own tabs. Logged only when the contents actually
    /// change, not on every poll.
    ///
    /// This exists to be checked against the game by hand. IsItemInCabinet is keyed by Cabinet
    /// sheet row rather than by item id, and a mapping that were off by anything would still
    /// return a plausible-looking total - a per-tab breakdown that has to match what the Armoire
    /// itself shows is what can actually catch that.
    /// </summary>
    private static void LogBreakdown(HashSet<uint> stored)
    {
        if (_cabinetRows == null)
            return;

        _categoryNames ??= Plugin.DataManager.GetExcelSheet<CabinetCategory>()!
            .Where(row => row.Category.RowId != 0)
            .ToDictionary(
                row => row.RowId,
                row => Plugin.DataManager.GetExcelSheet<Addon>()!.TryGetRow(row.Category.RowId, out var addon)
                    ? addon.Text.ExtractText()
                    : $"Category {row.RowId}");

        var byCategory = _cabinetRows
            .Where(row => stored.Contains(row.ItemId))
            .GroupBy(row => row.Category)
            .OrderBy(group => group.Key);

        foreach (var group in byCategory)
            Plugin.Log.Debug($"Armoire category {_categoryNames.GetValueOrDefault(group.Key, $"Category {group.Key}")}: {group.Count()}");
    }

    /// <summary>
    /// Order-insensitive fingerprint, like the dresser's. A HashSet has no meaningful order to
    /// begin with, so this could not have been anything else.
    /// </summary>
    private static long SignatureOf(HashSet<uint> itemIds)
    {
        long sum = 0;
        foreach (var id in itemIds)
            sum += id;

        return itemIds.Count * 1_000_000_007L + sum;
    }

    /// <summary>
    /// The item ids currently in the Armoire. Empty while <see cref="HasData"/> is false, which
    /// callers have to distinguish from a genuinely empty Armoire themselves.
    /// </summary>
    public static HashSet<uint> GetStoredItemIds()
    {
        lock (LockObject)
        {
            // A snapshot, so a poll landing mid-rebuild cannot mutate what the window is reading.
            return new HashSet<uint>(_cachedItemIds);
        }
    }

    private static void SwitchCharacter(ulong contentId)
    {
        lock (LockObject)
        {
            _contentId = contentId;
            _cachedItemIds = [];
            _cachedSignature = -1;
        }

        _hasData = false;
        _fromSavedCache = false;
        Interlocked.Increment(ref _generation);

        if (contentId == 0)
        {
            Plugin.Log.Information("Logged out - Armoire cache cleared");
            return;
        }

        if (!TryLoad(contentId, out var itemIds, out var savedAt))
        {
            Plugin.Log.Information($"No saved Armoire for character {contentId:X16}");
            return;
        }

        lock (LockObject)
        {
            _cachedItemIds = itemIds;
            _cachedSignature = SignatureOf(itemIds);
        }

        // Set before the flags, so a reader that sees them always sees a valid timestamp.
        _savedAt = savedAt;
        _hasData = true;
        _fromSavedCache = true;
        Interlocked.Increment(ref _generation);
        Plugin.Log.Information($"Loaded {itemIds.Count} Armoire items saved {savedAt:u} for character {contentId:X16}");
    }

    private static string PathFor(ulong contentId)
    {
        var dir = Plugin.PluginInterface.ConfigDirectory.FullName;
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"armoire-{contentId:X16}.json");
    }

    private static void Save()
    {
        HashSet<uint> itemIds;
        ulong contentId;
        lock (LockObject)
        {
            itemIds = new HashSet<uint>(_cachedItemIds);
            contentId = _contentId;
        }

        if (contentId == 0)
            return;

        try
        {
            var payload = new CacheFile
            {
                Version = CacheFormatVersion,
                SavedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                // An empty Armoire is saved as an empty list rather than skipped: the file
                // existing is what records that this character's Armoire has been read at all.
                ItemIds = [.. itemIds],
            };

            // Temp file then move, so a crash mid-write cannot leave a half-written cache.
            var path = PathFor(contentId);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(payload));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not save the Armoire cache");
        }
    }

    private static bool TryLoad(ulong contentId, out HashSet<uint> itemIds, out DateTimeOffset savedAt)
    {
        itemIds = [];
        savedAt = default;

        try
        {
            var path = PathFor(contentId);
            if (!File.Exists(path))
                return false;

            var payload = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(path));
            if (payload == null || payload.Version != CacheFormatVersion)
                return false;

            itemIds = [.. payload.ItemIds];
            savedAt = DateTimeOffset.FromUnixTimeSeconds(payload.SavedAtUnix);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not read the saved Armoire cache - it will be rebuilt");
            return false;
        }
    }

    private class CacheFile
    {
        public int Version { get; set; }
        public long SavedAtUnix { get; set; }
        public List<uint> ItemIds { get; set; } = [];
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Plugin.Framework.Update -= OnFrameworkUpdate;
        _disposed = true;
    }
}
