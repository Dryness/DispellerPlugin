using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Dispeller.Services;

public class DresserScanner : IDisposable
{
    private static readonly object LockObject = new();
    private static List<PrismBoxItem> _cachedDresserItems = [];
    private static int _dresserItemSlotsUsed = 0;
    private static long _cachedSignature = -1;
    private static int _generation = 0;

    /// <summary>
    /// Bumped every time the cached contents actually change. The window watches this so it
    /// can rebuild its results when the dresser is opened, re-sorted, or deposited into,
    /// without the user pressing Scan again.
    /// </summary>
    public static int Generation => Volatile.Read(ref _generation);

    // The dresser is re-read on a timer rather than on a change signal, because the game
    // gives us nothing reliable to watch. UsedSlots was used for this and does not track
    // the contents: two reads both reporting 702 returned completely different items. A
    // full pass over the 8000-entry array is cheap, so polling twice a second is simpler
    // and more dependable than trying to detect the change.
    private const int PollIntervalFrames = 30;
    private int _framesSincePoll = PollIntervalFrames;

    private bool _disposed = false;

    public DresserScanner()
    {
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    private unsafe void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            var agent = AgentMiragePrismPrismBox.Instance();
            if (agent == null || !agent->IsAddonReady() || agent->Data == null)
            {
                // Arm the next poll so opening the dresser reads it on the first frame the
                // addon is ready, rather than waiting out the interval.
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
            Plugin.Log.Information($"Dresser cache updated: {items.Count} items (generation {Generation})");
        }
        catch
        {
            // Silently handle exceptions in framework update to avoid spam
            // Errors will be logged if they occur during manual refresh
        }
    }

    /// <summary>
    /// Reads every non-empty entry in the array. UsedSlots is NOT the live item count: with
    /// UsedSlots at 702 the array held 1212 non-zero entries, and the 510 past that boundary
    /// were real, distinct items - every boot and every accessory among them.
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
                // Don't store name from dresser data - it can be incorrect/outdated
                // Name will be retrieved from Lumina in MainWindow for accuracy
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
    /// Order-insensitive fingerprint of the contents. The array reorders itself as the
    /// dresser's view changes, and a reshuffle of the same items produces the same results -
    /// so ordering must not count as a change, or the window would rebuild for nothing.
    /// </summary>
    private static long SignatureOf(List<PrismBoxItem> items)
    {
        long sum = 0;
        foreach (var item in items)
            sum += item.ItemId;

        return items.Count * 1_000_000_007L + sum;
    }

    public static List<PrismBoxItem> GetDresserItems()
    {
        lock (LockObject)
        {
            // Return a snapshot copy to prevent race conditions if cache updates during scan
            return new List<PrismBoxItem>(_cachedDresserItems);
        }
    }

    public static unsafe bool TryRefresh()
    {
        try
        {
            var agent = AgentMiragePrismPrismBox.Instance();
            if (agent == null)
            {
                Plugin.Log.Debug("TryRefresh: AgentMiragePrismPrismBox.Instance() returned null - dresser not open");
                return false;
            }

            if (!agent->IsAddonReady())
            {
                Plugin.Log.Debug("TryRefresh: Agent is not ready (IsAddonReady = false) - dresser not open");
                return false;
            }

            if (agent->Data == null)
            {
                Plugin.Log.Debug("TryRefresh: Agent data is null - dresser not initialized");
                return false;
            }

            var items = ReadAll(agent);
            var signature = SignatureOf(items);

            bool changed;
            lock (LockObject)
            {
                changed = _cachedDresserItems.Count == 0 || signature != _cachedSignature;
                _cachedDresserItems = items;
                _cachedSignature = signature;
                _dresserItemSlotsUsed = agent->Data->UsedSlots;
            }

            if (changed)
                Interlocked.Increment(ref _generation);

            Plugin.Log.Information($"TryRefresh: Loaded {items.Count} items from dresser (UsedSlots: {_dresserItemSlotsUsed})");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error in TryRefresh");
            return false;
        }
    }

    public static bool HasCachedData()
    {
        lock (LockObject)
        {
            var hasData = _cachedDresserItems.Count > 0;
            if (hasData)
            {
                Plugin.Log.Debug($"HasCachedData: Cache contains {_cachedDresserItems.Count} items");
            }
            return hasData;
        }
    }

    public static int GetCachedItemCount()
    {
        lock (LockObject)
        {
            return _cachedDresserItems.Count;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Plugin.Framework.Update -= OnFrameworkUpdate;
        _disposed = true;
    }
}

public class PrismBoxItem
{
    public string Name { get; set; } = string.Empty;
    public uint Slot { get; set; }
    public uint ItemId { get; set; }
    public uint IconId { get; set; }
    public byte Stain1 { get; set; }
    public byte Stain2 { get; set; }
}
