using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Lumina.Excel.Sheets;

namespace Dispeller.Services;

/// <summary>
/// The game's outfit sets - the "... Attire" bundles the Glamour Dresser groups pieces under.
///
/// <c>MirageStoreSetItem</c> is keyed by the bundle's own item id and names the piece filling
/// each equipment slot. It is the only complete answer to "what is in this outfit": the
/// dresser's own <c>Slot</c> field groups a bundle with the pieces of it you hold, but a piece
/// belonging to two sets carries only one Slot - 471 pieces game-wide do - so Slot alone
/// attributes those to the wrong set half the time.
/// </summary>
internal static class OutfitService
{
    private static Dictionary<uint, uint[]>? components;

    /// <summary>
    /// Every outfit set in the game: bundle item id to the item ids of its pieces. Built once
    /// and kept - it is 1170 rows of static sheet data and cannot change while the game runs.
    /// </summary>
    private static Dictionary<uint, uint[]> Components => components ??= BuildComponents();

    private static Dictionary<uint, uint[]> BuildComponents()
    {
        var sheet = Plugin.DataManager.GetExcelSheet<MirageStoreSetItem>()!;
        var built = new Dictionary<uint, uint[]>();

        foreach (var row in sheet)
        {
            // Row 0 is a placeholder that answers happily and means nothing - its Head points
            // at Gil. Left in, it would make item id 1 a component of an outfit.
            if (row.RowId == 0)
                continue;

            uint[] pieces =
            [
                row.MainHand.RowId, row.OffHand.RowId, row.Head.RowId, row.Body.RowId,
                row.Hands.RowId, row.Legs.RowId, row.Feet.RowId, row.Earrings.RowId,
                row.Necklace.RowId, row.Bracelets.RowId, row.Ring.RowId,
            ];

            // A zero means the outfit has no piece for that slot, not that the row is bad.
            var filled = pieces.Where(id => id != 0).ToArray();
            if (filled.Length > 0)
                built[row.RowId] = filled;
        }

        return built;
    }

    /// <summary>
    /// Builds the index now rather than on the first question asked of it, so the framework thread
    /// finds it already there. This does the work rather than merely warming the pages it reads
    /// from - the index is static sheet data and cannot go stale.
    ///
    /// Racing the framework thread here is harmless: the loser builds an identical dictionary and
    /// the reference assignment discards it.
    /// </summary>
    internal static long Warm(CancellationToken token)
    {
        if (token.IsCancellationRequested)
            return 0;

        // Summed and returned so the caller has something observable to log - a build whose result
        // went nowhere would be a read the compiler is free to drop.
        long touched = Components.Count;
        foreach (var pieces in Components.Values)
            touched += pieces.Length;

        return touched;
    }

    /// <summary>
    /// The pieces of one outfit, empty for an item that is not an outfit set. Ids are base ids
    /// - the sheet has no notion of the dresser's HQ offset.
    /// </summary>
    public static IReadOnlyList<uint> GetComponents(uint setItemId)
        => Components.TryGetValue(setItemId, out var pieces) ? pieces : [];
}
