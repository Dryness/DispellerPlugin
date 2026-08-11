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
/// What the character has stored in their Armoire, as a set of item ids.
///
/// Separate from <see cref="DresserScanner"/> despite the same shape: the two stores go stale
/// independently, and the window has to be able to say which of them is out of date.
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

    // Bumped to discard every cache written before the read gate was corrected. Those may hold a
    // partial read, which cannot be told from a real one once it is on disk.
    private const int CacheFormatVersion = 2;

    /// <summary>
    /// False until the Armoire has been read once for this character, live or off disk. Not the
    /// same as "the Armoire is empty" - an unread store must not be drawn as an empty one.
    /// </summary>
    public static bool HasData => _hasData;

    /// <summary>
    /// True when the cached set came off disk and has not been confirmed against the game this
    /// session. The character may have stored or withdrawn while the plugin was off.
    /// </summary>
    public static bool IsFromSavedCache => _fromSavedCache;

    /// <summary>When the cache on disk was written. Only meaningful while <see cref="IsFromSavedCache"/>.</summary>
    public static DateTimeOffset SavedAt => _savedAt;

    /// <summary>
    /// Bumped every time the stored set actually changes. MainWindow watches this exactly as it
    /// watches <see cref="DresserScanner.Generation"/>.
    /// </summary>
    public static int Generation => Volatile.Read(ref _generation);

    // The game offers no change signal, so the set is polled while an Armoire window is open.
    // Storing and withdrawing both happen through those windows, so that is enough to keep up.
    private const int PollIntervalFrames = 30;

    // How long a window has to have been open, and how many consecutive polls have to agree,
    // before a read taken through it may be committed.
    //
    // The bitfield arrives over several frames once a window asks for it, and IsCabinetLoaded()
    // can report true part-way through with nothing to tell a partial set from a complete one.
    //
    // Both halves are needed. Waiting alone still reads whatever is there at the deadline;
    // agreement alone confirms a partial set against itself, since reads taken before the data
    // lands agree perfectly. Together they say the set stopped growing and then stayed that way.
    //
    // Owed once per character per session - see _bitfieldComplete.
    private const int SettleFrames = 120;
    private const int AgreeingPollsRequired = 2;

    private int _framesSincePoll = PollIntervalFrames;

    // Per-open state, cleared every time the gate closes so each visit earns its read from
    // scratch rather than inheriting confidence from the last one.
    private int _framesGateOpen;
    private string? _openGate;
    private long _candidateSignature = -1;
    private int _candidateCount;
    private int _agreeingPolls;
    private bool _settledThisOpen;

    /// <summary>
    /// True once a read has settled for this character this session - the point at which the whole
    /// bitfield is known to have arrived.
    ///
    /// The settle wait exists to catch a partially-arrived set, and the client does not un-receive
    /// what it has received, so the wait is owed once rather than once per visit. Making a later
    /// store or withdraw serve it is how a brief visit loses the change that prompted it.
    ///
    /// Per character, because the bitfield belongs to the logged-in character.
    /// </summary>
    private static volatile bool _bitfieldComplete;

    // Cabinet sheet row -> item id, built once. IsItemInCabinet is keyed by sheet row rather than
    // by item id, so the mapping has to be walked in that direction, and the sheet is far too
    // large to redo per poll. The category rides along for the log breakdown.
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
            // Same character watch as the dresser's: this covers login, logout (id 0) and the
            // plugin being enabled mid-session in one place.
            var contentId = Plugin.ClientState.IsLoggedIn ? Plugin.PlayerState.ContentId : 0;
            if (contentId != _contentId)
            {
                SwitchCharacter(contentId);

                // Whatever was mid-settle described the character being left. Closed without a
                // UIState so the final-read path cannot fire against the wrong content id.
                CloseGate(null);
            }

            if (contentId == 0)
                return;

            // Read only while a window that displays Armoire contents is open, and only commit
            // once that read has settled. See OpenGate for which windows qualify.
            var uiState = UIState.Instance();
            var gate = OpenGate();

            if (gate == null || uiState == null)
            {
                // The window has gone. CloseGate takes a final read on the way out when the set is
                // already known complete, so a change made since the last poll is not lost.
                CloseGate(uiState);
                return;
            }

            if (_openGate == null)
            {
                _openGate = gate;
                Plugin.Log.Debug($"Armoire window open ({gate})");
            }

            // The window is open and the client is fetching - Cabinet.State runs
            // Loaded -> Requested -> Loaded when "Store an item" is opened directly. That is not
            // the window closing, so the gate stays open, but the settle restarts: the clock
            // should measure from when the data lands.
            if (!uiState->Cabinet.IsCabinetLoaded())
            {
                RestartSettle();
                return;
            }

            _framesGateOpen++;

            // Armed to fire on the first frame the gate opens, so the settle has an early
            // candidate to compare later reads against.
            if (++_framesSincePoll < PollIntervalFrames)
                return;
            _framesSincePoll = 0;

            // An empty Armoire is a legitimate answer here, unlike an empty dresser read: the gate
            // establishes that the data is real, so empty means empty.
            var itemIds = ReadAll(uiState);
            var signature = SignatureOf(itemIds);

            if (signature == _candidateSignature)
            {
                _agreeingPolls++;
            }
            else
            {
                // A change mid-settle is the rest of the data arriving, which is what the wait is
                // for, and nothing the game exposes says it happened.
                if (_candidateSignature != -1)
                    Plugin.Log.Debug(
                        $"Armoire read changed from {_candidateCount} to {itemIds.Count} items "
                        + $"{_framesGateOpen} frames after opening ({gate}) - settle restarted");

                _candidateSignature = signature;
                _candidateCount = itemIds.Count;
                _agreeingPolls = 1;
            }

            // Owed once per session. After that the set is known complete, so a change is a store
            // or a withdraw and commits on the first poll that sees it.
            if (!_bitfieldComplete && (_framesGateOpen < SettleFrames || _agreeingPolls < AgreeingPollsRequired))
                return;

            if (!_settledThisOpen)
            {
                _settledThisOpen = true;

                if (!_bitfieldComplete)
                {
                    _bitfieldComplete = true;
                    Plugin.Log.Information(
                        $"Armoire read settled at {itemIds.Count} items after {_framesGateOpen} frames "
                        + $"via {gate} (cabinet state {uiState->Cabinet.State})");
                }
            }

            Commit(itemIds, signature, uiState, gate);
        }
        catch
        {
            // Swallowed for the same reason DresserScanner swallows: this runs every frame, so a
            // recurring fault would flood the log. The next poll retries from scratch.
        }
    }

    /// <summary>
    /// Drops what was learned while the gate was open, so the next visit starts from nothing.
    ///
    /// A candidate that never settled is discarded rather than kept as a best guess: an unread or
    /// stale Armoire says so in the window, where a partial read flagged live says nothing at all
    /// and is wrong on every row it is missing.
    ///
    /// Once the set is known complete the closing frames are worth reading, though - the player
    /// may have stored or withdrawn since the last poll - so a final read is taken. It stays valid
    /// after the addon has gone, because the bitfield lives on UIState rather than on the agent.
    ///
    /// <paramref name="uiState"/> is null when there is nothing to read against, and deliberately
    /// null on a character switch, where the read would belong to the character being left.
    /// </summary>
    private unsafe void CloseGate(UIState* uiState)
    {
        var gate = _openGate;

        if (gate != null && _bitfieldComplete && uiState != null && uiState->Cabinet.IsCabinetLoaded())
        {
            var itemIds = ReadAll(uiState);
            Commit(itemIds, SignatureOf(itemIds), uiState, $"{gate}, closing");
        }
        else if (gate != null && !_settledThisOpen)
        {
            Plugin.Log.Information(
                $"Armoire window ({gate}) closed after {_framesGateOpen} frames without settling - "
                + (_candidateSignature == -1
                    ? "no read taken"
                    : $"last read of {_candidateCount} items discarded"));
        }

        _openGate = null;
        _settledThisOpen = false;
        RestartSettle();
    }

    /// <summary>
    /// Clears the settle clock and its candidate, leaving the gate itself open.
    ///
    /// Deliberately does not touch <c>_settledThisOpen</c>: a read that settled earlier in this
    /// open really did settle, and clearing it would report the eventual close as having lost
    /// something it did not.
    /// </summary>
    private void RestartSettle()
    {
        _framesGateOpen = 0;
        _candidateSignature = -1;
        _candidateCount = 0;
        _agreeingPolls = 0;

        // Armed, so the frame after this is read rather than up to half a second later.
        _framesSincePoll = PollIntervalFrames;
    }

    /// <summary>
    /// Writes a read into the cache. Shared by the poll and the final read on close so the two
    /// cannot drift apart.
    /// </summary>
    private static unsafe void Commit(HashSet<uint> itemIds, long signature, UIState* uiState, string gate)
    {
        // Withdrawing is one item at a time, so a large drop in a single read is an incomplete
        // read rather than a player emptying their Armoire. Not refused - that would need a rule
        // for when to stop refusing - but said out loud.
        var previousCount = _cachedItemIds.Count;
        if (previousCount > 0 && itemIds.Count * 4 < previousCount * 3)
            Plugin.Log.Warning(
                $"Armoire read via {gate} dropped from {previousCount} to {itemIds.Count} items "
                + $"(cabinet state {uiState->Cabinet.State}) - suspect an incomplete read");

        // A successful read confirms the cache whether or not anything changed, so the "cached"
        // notice clears even when the saved copy was accurate.
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
        Plugin.Log.Information(
            $"Armoire cache updated: {itemIds.Count} items stored via {gate}, "
            + $"cabinet state {uiState->Cabinet.State} (generation {Generation})");
        LogBreakdown(itemIds);
    }

    /// <summary>
    /// The window, if any, that licenses a read this frame - or null to leave the cache alone.
    ///
    /// Only the two windows that display Armoire contents qualify. "Store an item" and "Remove an
    /// item" are separate agents with separate addons, so covering one does nothing for the other.
    ///
    /// The Glamour Dresser is deliberately not a third door: it does not display Armoire contents,
    /// so it never makes the client fetch them and a read through it takes whatever happens to be
    /// there. Excluding it costs nothing, since the stored set only changes by storing or
    /// withdrawing and both happen behind these two agents. Nor does it strand anyone at the
    /// dresser - Edit Glamour Plates -> Open Armoire drives AgentCabinetWithdraw.
    ///
    /// Named rather than boolean so the log can say which door a read came through.
    /// </summary>
    private static unsafe string? OpenGate()
    {
        var store = AgentCabinet.Instance();
        if (store != null && store->IsAddonReady())
            return "Armoire (store)";

        var withdraw = AgentCabinetWithdraw.Instance();
        if (withdraw != null && withdraw->IsAddonReady())
            return "Armoire (remove)";

        return null;
    }

    /// <summary>
    /// Cabinet sheet rows that carry an item. Rows with none attached are placeholders and are
    /// skipped: they answer the lookup happily and would add item id 0 to the set.
    /// </summary>
    private static (uint CabinetRow, uint ItemId, uint Category)[] CabinetRows()
        => _cabinetRows ??= Plugin.DataManager.GetExcelSheet<CabinetSheet>()!
            .Where(row => row.Item.RowId != 0)
            .Select(row => (row.RowId, row.Item.RowId, row.Category.RowId))
            .ToArray();

    /// <summary>
    /// Asks the game about every cabinet row. The item id is what comes back, because that is the
    /// identity the rest of the plugin is keyed on.
    /// </summary>
    private static unsafe HashSet<uint> ReadAll(UIState* uiState)
    {
        var stored = new HashSet<uint>();

        foreach (var (cabinetRow, itemId, _) in CabinetRows())
        {
            if (uiState->Cabinet.IsItemInCabinet(cabinetRow))
                stored.Add(itemId);
        }

        return stored;
    }

    /// <summary>
    /// The result split by the Armoire's own tabs, logged only when the contents change.
    ///
    /// Exists to be checked against the game by hand: IsItemInCabinet is keyed by sheet row rather
    /// than by item id, and a mapping that were off would still return a plausible-looking total.
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

    /// <summary>Order-insensitive fingerprint of the stored set.</summary>
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

        // The incoming character's bitfield is their own, so the settle wait is owed again.
        _bitfieldComplete = false;
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
