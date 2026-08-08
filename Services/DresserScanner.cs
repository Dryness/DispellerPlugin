using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using System;
using System.Collections.Generic;

namespace Dispeller.Services;

public class DresserScanner : IDisposable
{
    private static readonly object LockObject = new();
    private static List<PrismBoxItem> _cachedDresserItems = [];
    private static int _dresserItemSlotsUsed = 0;

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
            if (agent == null)
                return;

            if (!agent->IsAddonReady() || agent->Data == null)
                return;

            var usedSlots = agent->Data->UsedSlots;

            // Always cache if cache is empty, or if the slot count has changed
            bool shouldUpdate = false;
            lock (LockObject)
            {
                shouldUpdate = _cachedDresserItems.Count == 0 || usedSlots != _dresserItemSlotsUsed;
            }
            
            if (!shouldUpdate)
                return;

            lock (LockObject)
            {
                var wasEmpty = _cachedDresserItems.Count == 0;
                _cachedDresserItems.Clear();
                
                // Only [0, UsedSlots) holds live items. The array is 8000 entries long and the
                // game never zeroes an entry when an item is retrieved, so everything past
                // UsedSlots is an abandoned leftover of a previously stored item.
                var items = agent->Data->PrismBoxItems;
                var itemCount = 0;
                for (var i = 0; i < usedSlots && i < items.Length; i++)
                {
                    var item = items[i];
                    if (item.ItemId == 0)
                        continue;

                    _cachedDresserItems.Add(new PrismBoxItem
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
                    itemCount++;
                }

                _dresserItemSlotsUsed = usedSlots;

                if (itemCount > 0)
                {
                    // UsedSlots should match itemCount - they come from different fields, so a
                    // mismatch means the struct layout drifted out from under FFXIVClientStructs.
                    Plugin.Log.Information($"OnFrameworkUpdate: Cached {itemCount} items from dresser (UsedSlots: {usedSlots}, cache was empty: {wasEmpty})");
                }
            }
        }
        catch
        {
            // Silently handle exceptions in framework update to avoid spam
            // Errors will be logged if they occur during manual refresh
        }
    }

    public static unsafe List<PrismBoxItem> GetDresserItems()
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

            lock (LockObject)
            {
                _cachedDresserItems.Clear();

                // See OnFrameworkUpdate: entries at or past UsedSlots are stale leftovers.
                var usedSlots = agent->Data->UsedSlots;
                var items = agent->Data->PrismBoxItems;
                var itemCount = 0;
                for (var i = 0; i < usedSlots && i < items.Length; i++)
                {
                    var item = items[i];
                    if (item.ItemId == 0)
                        continue;

                    _cachedDresserItems.Add(new PrismBoxItem
                    {
                        Name = string.Empty,
                        Slot = item.Slot,
                        ItemId = item.ItemId,
                        IconId = item.IconId,
                        Stain1 = item.Stains[0],
                        Stain2 = item.Stains[1],
                    });
                    itemCount++;
                }

                // Update the used slots counter to prevent immediate re-trigger
                _dresserItemSlotsUsed = usedSlots;

                Plugin.Log.Information($"TryRefresh: Loaded {itemCount} items from dresser (UsedSlots: {_dresserItemSlotsUsed})");
                LogSlotDistribution(agent->Data);
                return true;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error in TryRefresh");
            return false;
        }
    }

    /// <summary>
    /// Diagnostic for the UsedSlots/itemCount mismatch. The backing array holds 8000 entries
    /// but the dresser itself caps at 800, so a raw non-zero ItemId count cannot be the real
    /// figure. This reports how the non-zero entries sit relative to UsedSlots, which tells us
    /// whether the live items are packed into [0, UsedSlots) or scattered among stale ones.
    /// </summary>
    private static unsafe void LogSlotDistribution(MiragePrismPrismBoxData* data)
    {
        var items = data->PrismBoxItems;
        var usedSlots = data->UsedSlots;

        int nonZeroTotal = 0, inUsedRange = 0, beyondUsedRange = 0, highestNonZero = -1;
        uint minSlot = uint.MaxValue, maxSlot = 0;
        var distinctItemIds = new HashSet<uint>();
        var distinctSlotItemPairs = new HashSet<(uint Slot, uint ItemId)>();
        var beyondSamples = new List<string>();

        for (var i = 0; i < items.Length; i++)
        {
            var itemId = items[i].ItemId;
            if (itemId == 0)
                continue;

            var slot = items[i].Slot;
            nonZeroTotal++;
            highestNonZero = i;
            distinctItemIds.Add(itemId);
            distinctSlotItemPairs.Add((slot, itemId));
            minSlot = Math.Min(minSlot, slot);
            maxSlot = Math.Max(maxSlot, slot);

            if (i < usedSlots)
            {
                inUsedRange++;
            }
            else
            {
                beyondUsedRange++;
                if (beyondSamples.Count < 6)
                    beyondSamples.Add($"[idx {i}] Slot={slot} ItemId={itemId}");
            }
        }

        Plugin.Log.Information(
            $"SlotDistribution: ArrayLength={items.Length} UsedSlots={usedSlots} NonZeroTotal={nonZeroTotal} " +
            $"InUsedRange={inUsedRange} BeyondUsedRange={beyondUsedRange} HighestNonZeroIndex={highestNonZero}");
        Plugin.Log.Information(
            $"SlotDistribution: DistinctItemIds={distinctItemIds.Count} DistinctSlotItemPairs={distinctSlotItemPairs.Count} " +
            $"SlotRange={(nonZeroTotal == 0 ? "n/a" : $"{minSlot}..{maxSlot}")}");

        if (beyondSamples.Count > 0)
            Plugin.Log.Information($"SlotDistribution: first beyond UsedSlots -> {string.Join(" | ", beyondSamples)}");
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
