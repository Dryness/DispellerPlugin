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
/// The first <c>BuildGroups</c> of a session costs far more than every one after it for the same
/// data, so the cost is warmup rather than work: Lumina paging <c>.exd</c> data in, and the JIT
/// compiling the pipeline. Paging here is the only way to remove that hitch rather than relocate
/// it - a framework handler would be the game's main thread too.
///
/// This only ever reads, and only sheets the game treats as immutable static data. Lumina caches
/// sheets in a <c>ConcurrentDictionary</c>, so the worst a race with the framework thread can do
/// is build one twice and throw one away. Nothing here is load-bearing: if it fails, or never gets
/// to run, the first build simply pays what it paid before.
/// </summary>
internal static class SheetWarmup
{
    // Static state outlives the plugin instance whenever Dalamud reuses the assembly - a
    // disable/enable, or a dev reload landing in the same load context. So this has to be a cycle
    // rather than a latch: Stop() undoes what Start() did, down to the one-shot flag, or the
    // second load of a session silently gets no warmup.
    private static readonly object Gate = new();
    private static bool started;
    private static CancellationTokenSource? cancellation;

    /// <summary>
    /// Kicks the warmup off once per load. Safe to call more than once; later calls do nothing
    /// until <see cref="Stop"/> has run.
    /// </summary>
    public static void Start()
    {
        CancellationToken token;

        lock (Gate)
        {
            if (started)
            {
                Plugin.Log.Debug("Sheet warmup already running - not started again");
                return;
            }

            started = true;
            cancellation = new CancellationTokenSource();
            token = cancellation.Token;
        }

        // The token is deliberately not passed to Task.Run: that would complete the task as
        // Cancelled, and nothing awaits this one. Run() handles the token and returns normally,
        // which leaves nothing unobserved behind.
        _ = Task.Run(() => Run(token));
        Plugin.Log.Debug("Sheet warmup started");
    }

    /// <summary>
    /// Stops the warmup if it is still going, so an unloading plugin doesn't leave it running, and
    /// puts the class back where <see cref="Start"/> found it.
    /// </summary>
    public static void Stop()
    {
        CancellationTokenSource? cts;

        lock (Gate)
        {
            if (!started)
                return;

            started = false;
            cts = cancellation;
            cancellation = null;
        }

        cts?.Cancel();

        // Safe to dispose straight after cancelling: the running task only reads
        // IsCancellationRequested off its captured token, which stays readable once disposed.
        cts?.Dispose();
        Plugin.Log.Debug("Sheet warmup stopped");
    }

    private static void Run(CancellationToken token)
    {
        try
        {
            var total = Stopwatch.StartNew();

            var item = Warm("Item", WarmItems, token);
            var cabinet = Warm("Cabinet", WarmCabinet, token);
            var outfits = Warm("MirageStoreSetItem", OutfitService.Warm, token);

            if (token.IsCancellationRequested)
            {
                Plugin.Log.Debug(
                    $"Sheet warmup cancelled after {total.Elapsed.TotalMilliseconds:F1} ms - "
                    + "the next load will warm from scratch");
                return;
            }

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
