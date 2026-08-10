using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Lumina.Excel.Sheets;

namespace Dispeller.Services;

/// <summary>
/// Pages the Excel sheets the results are built from into memory, on a background thread, before
/// anything on the framework thread asks for them.
///
/// The first <c>BuildGroups</c> of a session costs about 25-40ms and every one after it costs
/// 1-2ms for the same data, so the cost is warmup rather than work - measured at 23.5ms for 426
/// items and 2.0ms for 1636 half a second later. It is Lumina paging <c>.exd</c> data in and the
/// JIT compiling the pipeline, not the item count. Doing the paging here means the framework
/// thread finds the sheets already resident, which is the only way to remove that hitch rather
/// than relocate it - a framework handler would be the game's main thread too.
///
/// This only ever reads, and it reads sheets the game treats as immutable static data. Lumina
/// caches sheets in a <c>ConcurrentDictionary</c>, so the worst a race with the framework thread
/// can do is build one twice and throw one away. Nothing here is load-bearing: if it fails, or
/// never gets to run, the first build simply pays what it paid before.
/// </summary>
internal static class SheetWarmup
{
    private static int started;
    private static readonly CancellationTokenSource Cancellation = new();

    /// <summary>Kicks the warmup off once. Safe to call more than once; later calls do nothing.</summary>
    public static void Start()
    {
        if (Interlocked.Exchange(ref started, 1) != 0)
            return;

        Task.Run(() => Run(Cancellation.Token), Cancellation.Token);
    }

    /// <summary>Stops the warmup if it is still going, so an unloading plugin doesn't leave it running.</summary>
    public static void Stop() => Cancellation.Cancel();

    private static void Run(CancellationToken token)
    {
        try
        {
            var total = Stopwatch.StartNew();

            var item = Warm("Item", WarmItems, token);
            var cabinet = Warm("Cabinet", WarmCabinet, token);
            var outfits = Warm("MirageStoreSetItem", OutfitService.Warm, token);

            if (token.IsCancellationRequested)
                return;

            Plugin.Log.Information(
                $"Sheet warmup finished in {total.Elapsed.TotalMilliseconds:F1} ms "
                + $"(Item {item:F1} ms, Cabinet {cabinet:F1} ms, outfits {outfits:F1} ms)");
        }
        catch (Exception ex)
        {
            // Warming is an optimisation and nothing depends on it having happened, so a failure
            // is worth a line in the log and nothing more.
            Plugin.Log.Warning(ex, "Sheet warmup did not finish - the first scan will be slower");
        }
    }

    private static double Warm(string name, Func<CancellationToken, long> body, CancellationToken token)
    {
        if (token.IsCancellationRequested)
            return 0;

        var timer = Stopwatch.StartNew();
        var touched = body(token);
        timer.Stop();

        // The accumulator is logged rather than discarded so nothing here can be optimised away
        // as a read with no observable effect.
        Plugin.Log.Debug($"Warmed {name} in {timer.Elapsed.TotalMilliseconds:F1} ms (checksum {touched})");
        return timer.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// The Item sheet, which is the expensive one - every field the build reads, on every row.
    /// Touching a field is what pulls its page in, so this walks the whole sheet rather than
    /// sampling: page boundaries are not something to guess at.
    /// </summary>
    private static long WarmItems(CancellationToken token)
    {
        var sheet = Plugin.DataManager.GetExcelSheet<Item>()!;
        long touched = 0;
        var row = 0;

        foreach (var entry in sheet)
        {
            if ((++row & 0x3FF) == 0 && token.IsCancellationRequested)
                break;

            touched += (long)entry.ModelMain;
            touched += entry.Icon;
            touched += entry.DyeCount;

            // Reading the name's length pulls its bytes in without allocating a string for every
            // row in the sheet. ExtractText is JIT-compiled off a handful of rows below instead.
            touched += entry.Name.ByteLength;

            // The slot lookup crosses into EquipSlotCategory, so that sheet gets warmed here too
            // rather than needing a pass of its own.
            if (!entry.EquipSlotCategory.IsValid || entry.EquipSlotCategory.RowId == 0)
                continue;

            var category = entry.EquipSlotCategory.Value;
            touched += category.MainHand + category.OffHand + category.Head + category.Body
                       + category.Gloves + category.Legs + category.Feet + category.Ears
                       + category.Neck + category.Wrists + category.FingerR + category.FingerL;
        }

        // Enough to compile the string path without paying for 45,000 throwaway strings.
        var sampled = 0;
        foreach (var entry in sheet)
        {
            touched += entry.Name.ExtractText().Length;
            if (++sampled == 16)
                break;
        }

        return touched;
    }

    /// <summary>
    /// The Cabinet sheet, which both the Armoire scanner and <c>CanGoInArmoire</c> walk in full on
    /// their first call. Its row references reach into the Item sheet, already resident by now.
    /// </summary>
    private static long WarmCabinet(CancellationToken token)
    {
        var sheet = Plugin.DataManager.GetExcelSheet<Cabinet>()!;
        long touched = 0;
        var row = 0;

        foreach (var entry in sheet)
        {
            if ((++row & 0xFF) == 0 && token.IsCancellationRequested)
                break;

            touched += entry.Item.RowId;
            touched += entry.Category.RowId;
        }

        return touched;
    }
}
