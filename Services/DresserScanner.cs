using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace Dispeller.Services;

/// <summary>
/// What the character is holding in their Glamour Dresser, cached and persisted per character.
/// </summary>
public class DresserScanner : IDisposable
{
    private static readonly object LockObject = new();
    private static List<PrismBoxItem> _cachedDresserItems = [];
    private static int _dresserItemSlotsUsed = 0;
    private static long _cachedSignature = -1;
    private static int _generation = 0;
    private static ulong _contentId = 0;
    private static volatile bool _fromSavedCache = false;
    private static DateTimeOffset _savedAt;

    /// <summary>
    /// True when the cached contents came off disk and have not been confirmed against the game
    /// this session. The dresser may have been used elsewhere, or changed while the plugin was
    /// disabled, so the window says so.
    /// </summary>
    public static bool IsFromSavedCache => _fromSavedCache;

    /// <summary>When the cache on disk was written. Only meaningful while <see cref="IsFromSavedCache"/>.</summary>
    public static DateTimeOffset SavedAt => _savedAt;

    /// <summary>
    /// The cache is written per character so the dresser need not be reopened every session.
    /// Keyed on content id, which is what makes the file safe to reuse - the Glamour Dresser
    /// belongs to the character rather than to the account.
    /// </summary>
    private const int CacheFormatVersion = 1;

    /// <summary>
    /// Bumped every time the cached contents actually change. The window watches this so it can
    /// rebuild when the dresser is opened, re-sorted, or deposited into.
    /// </summary>
    public static int Generation => Volatile.Read(ref _generation);

    /// <summary>
    /// The dresser is re-read on a timer rather than on a change signal, because the game gives
    /// nothing reliable to watch: <c>UsedSlots</c> does not track the contents, and two reads
    /// reporting the same figure can return completely different items. A full pass over the
    /// array is cheap, so polling is simpler and more dependable than detecting the change.
    /// </summary>
    private const int PollIntervalFrames = 30;

    private int _framesSincePoll = PollIntervalFrames;

    /// <summary>Raised on the frame the Glamour Dresser addon becomes readable.</summary>
    public event Action? DresserOpened;

    /// <summary>Raised on the frame the Glamour Dresser addon stops being readable.</summary>
    public event Action? DresserClosed;

    /// <summary>
    /// Edge state for the two events above. They must be edge-triggered rather than
    /// level-triggered: a handler reacting to "the dresser is open" every frame would reopen a
    /// window the instant the user closed it.
    /// </summary>
    private bool _addonWasReady = false;

    private bool _disposed = false;

    public DresserScanner()
    {
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    private unsafe void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            // Watching the logged-in character rather than the Login event covers logging out
            // (id 0) and the plugin being enabled mid-session, in one place.
            var contentId = Plugin.ClientState.IsLoggedIn ? Plugin.PlayerState.ContentId : 0;
            if (contentId != _contentId)
                SwitchCharacter(contentId);

            var agent = AgentMiragePrismPrismBox.Instance();
            var addonReady = agent != null && agent->IsAddonReady() && agent->Data != null;

            if (addonReady != _addonWasReady)
            {
                _addonWasReady = addonReady;

                if (addonReady)
                    DresserOpened?.Invoke();
                else
                    DresserClosed?.Invoke();
            }

            if (!addonReady)
            {
                // Armed, so opening the dresser reads it on the first frame the addon is ready
                // rather than waiting out the interval.
                _framesSincePoll = PollIntervalFrames;
                return;
            }

            if (++_framesSincePoll < PollIntervalFrames)
                return;
            _framesSincePoll = 0;

            var items = ReadAll(agent);

            // The array can be read before the game has filled it. Leave the cache alone and
            // try again on the next poll rather than caching an empty dresser.
            if (items.Count == 0)
                return;

            // A successful read confirms the cache whether or not anything changed, so the
            // "cached" notice clears even when the saved copy was accurate.
            _fromSavedCache = false;

            var signature = SignatureOf(items);
            lock (LockObject)
            {
                if (_cachedDresserItems.Count > 0 && signature == _cachedSignature)
                    return;

                _cachedDresserItems = items;
                _cachedSignature = signature;
                _dresserItemSlotsUsed = agent->Data->UsedSlots;
            }

            Interlocked.Increment(ref _generation);
            Save();
            Plugin.Log.Information($"Dresser cache updated: {items.Count} items (generation {Generation})");
        }
        catch
        {
            // Swallowed deliberately: this runs every frame, so a recurring fault would flood the
            // log. The next poll retries from scratch.
        }
    }

    /// <summary>
    /// Reads every non-empty entry in the array. The whole array is live and must be walked in
    /// full: <c>UsedSlots</c> is not the item count, and entries well past it are real, distinct
    /// items rather than leftovers.
    /// </summary>
    private static unsafe List<PrismBoxItem> ReadAll(AgentMiragePrismPrismBox* agent)
    {
        var items = agent->Data->PrismBoxItems;
        var result = new List<PrismBoxItem>();

        for (var i = 0; i < items.Length; i++)
        {
            var item = items[i];
            if (item.ItemId == 0)
                continue;

            result.Add(new PrismBoxItem
            {
                // The dresser's own name can be stale, so it is left empty and resolved from the
                // Item sheet where the row is built.
                Name = string.Empty,
                Slot = item.Slot,
                ItemId = item.ItemId,
                IconId = item.IconId,
                Stain1 = item.Stains[0],
                Stain2 = item.Stains[1],
            });
        }

        return result;
    }

    /// <summary>
    /// Order-insensitive fingerprint of the contents. The array reorders itself as the dresser's
    /// view changes, and a reshuffle of the same items produces the same results - so ordering
    /// must not count as a change, or the window would rebuild for nothing.
    /// </summary>
    private static long SignatureOf(List<PrismBoxItem> items)
    {
        long sum = 0;
        foreach (var item in items)
            sum += item.ItemId;

        return items.Count * 1_000_000_007L + sum;
    }

    /// <summary>The dresser's contents as last read.</summary>
    public static List<PrismBoxItem> GetDresserItems()
    {
        lock (LockObject)
        {
            // A snapshot, so a poll landing mid-rebuild cannot mutate what the window is reading.
            return new List<PrismBoxItem>(_cachedDresserItems);
        }
    }

    /// <summary>
    /// Drops whatever the previous character had cached and picks up the new character's saved
    /// copy, if there is one. A content id of 0 means logged out: the cache is emptied and
    /// nothing is loaded.
    /// </summary>
    private static void SwitchCharacter(ulong contentId)
    {
        lock (LockObject)
        {
            _contentId = contentId;
            _cachedDresserItems = [];
            _cachedSignature = -1;
            _dresserItemSlotsUsed = 0;
        }

        _fromSavedCache = false;
        Interlocked.Increment(ref _generation);

        if (contentId == 0)
        {
            Plugin.Log.Information("Logged out - dresser cache cleared");
            return;
        }

        if (!TryLoad(contentId, out var items, out var savedAt))
        {
            Plugin.Log.Information($"No saved dresser for character {contentId:X16}");
            return;
        }

        lock (LockObject)
        {
            _cachedDresserItems = items;
            _cachedSignature = SignatureOf(items);
        }

        // Set before the flag, so a reader that sees the flag always sees a valid timestamp.
        _savedAt = savedAt;
        _fromSavedCache = true;
        Interlocked.Increment(ref _generation);
        Plugin.Log.Information($"Loaded {items.Count} dresser items saved {savedAt:u} for character {contentId:X16}");
    }

    private static string PathFor(ulong contentId)
    {
        var dir = Plugin.PluginInterface.ConfigDirectory.FullName;
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"dresser-{contentId:X16}.json");
    }

    /// <summary>
    /// Persists the cache for the current character. Contents change only when the dresser is
    /// opened or altered, so this is not a hot path.
    /// </summary>
    private static void Save()
    {
        List<PrismBoxItem> items;
        ulong contentId;
        lock (LockObject)
        {
            items = new List<PrismBoxItem>(_cachedDresserItems);
            contentId = _contentId;
        }

        if (contentId == 0 || items.Count == 0)
            return;

        try
        {
            var payload = new CacheFile
            {
                Version = CacheFormatVersion,
                SavedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Items = items,
            };

            // Temp file then move, so a crash mid-write cannot leave a half-written cache.
            var path = PathFor(contentId);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(payload));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            // A cache that cannot be written is an inconvenience rather than a failure - the
            // dresser can always be read again.
            Plugin.Log.Warning(ex, "Could not save the dresser cache");
        }
    }

    private static bool TryLoad(ulong contentId, out List<PrismBoxItem> items, out DateTimeOffset savedAt)
    {
        items = [];
        savedAt = default;

        try
        {
            var path = PathFor(contentId);
            if (!File.Exists(path))
                return false;

            var payload = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(path));
            if (payload == null || payload.Version != CacheFormatVersion || payload.Items.Count == 0)
                return false;

            items = payload.Items;
            savedAt = DateTimeOffset.FromUnixTimeSeconds(payload.SavedAtUnix);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not read the saved dresser cache - it will be rebuilt");
            return false;
        }
    }

    private class CacheFile
    {
        public int Version { get; set; }
        public long SavedAtUnix { get; set; }
        public List<PrismBoxItem> Items { get; set; } = [];
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Plugin.Framework.Update -= OnFrameworkUpdate;
        _disposed = true;
    }
}

/// <summary>One entry as the Glamour Dresser stores it. <c>Slot</c> is the id of the dresser's own
/// grouping rather than a storage position, so a bundle and its pieces all share one.</summary>
public class PrismBoxItem
{
    public string Name { get; set; } = string.Empty;
    public uint Slot { get; set; }
    public uint ItemId { get; set; }
    public uint IconId { get; set; }
    public byte Stain1 { get; set; }
    public byte Stain2 { get; set; }
}
