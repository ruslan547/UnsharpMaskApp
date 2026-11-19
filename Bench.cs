using System.Diagnostics;
using UnsharpMaskApp.Core;

namespace UnsharpMaskApp;

/// <summary>Консольные режимы: замеры времени (--bench) и стресс-тест отмены/гонок (--stress).</summary>
internal static class Bench
{
    private static readonly UnsharpParams DefaultParams = new(1.5, 2.0, 0);

    /// <summary>--bench &lt;файл&gt; [повторов] [высота полосы]</summary>
    public static async Task<int> RunBenchmark(string[] args)
    {
        var src = await ImageIo.ReadAsync(args[0], CancellationToken.None);
        int repeats = args.Length > 1 ? int.Parse(args[1]) : 5;
        int strip = args.Length > 2 ? int.Parse(args[2]) : 32;
        Console.WriteLine($"Изображение {src.Width}x{src.Height}, полоса {strip} строк, ядер CPU: {Environment.ProcessorCount}, повторов: {repeats}");

        byte[]? reference = null;
        double t1 = 0;
        foreach (int pool in new[] { 1, 2, 4, 6, 8, 10, 12, 16 })
        {
            var times = new List<double>();
            bool identical = true;
            new ProcessingPipeline { PoolSize = pool, StripHeight = strip, Params = DefaultParams }.Run(src, null, default); // прогрев
            for (int i = 0; i < repeats; i++)
            {
                var pipeline = new ProcessingPipeline { PoolSize = pool, StripHeight = strip, Params = DefaultParams };
                var sw = Stopwatch.StartNew();
                var res = pipeline.Run(src, null, CancellationToken.None);
                times.Add(sw.Elapsed.TotalMilliseconds);
                reference ??= res.Pixels;
                identical &= res.Pixels.AsSpan().SequenceEqual(reference);
            }
            times.Sort();
            double median = times[times.Count / 2];
            if (pool == 1) t1 = median;
            Console.WriteLine($"pool={pool,2}  median={median,8:F1} ms  min={times[0],8:F1}  max={times[^1],8:F1}  " +
                              $"speedup={t1 / median,5:F2}  eff={t1 / median / pool * 100,5:F1}%  identical={identical}  alive={FixedThreadPool.AliveThreads}");
        }
        return 0;
    }

    /// <summary>--stress &lt;файл&gt; [итераций]: случайные размеры пула/полос и случайные моменты отмены.</summary>
    public static async Task<int> RunStress(string[] args)
    {
        var src = await ImageIo.ReadAsync(args[0], CancellationToken.None);
        int iterations = args.Length > 1 ? int.Parse(args[1]) : 500;
        var reference = new ProcessingPipeline { PoolSize = 1, StripHeight = 64, Params = DefaultParams }.Run(src, null, default).Pixels;
        var rnd = new Random(10);
        int completed = 0, cancelled = 0, mismatches = 0, hangs = 0, leaks = 0;
        var total = Stopwatch.StartNew();

        for (int i = 0; i < iterations; i++)
        {
            int pool = rnd.Next(1, 17), strip = rnd.Next(1, 129);
            using var cts = new CancellationTokenSource();
            if (rnd.Next(2) == 0) cts.CancelAfter(rnd.Next(0, 60));
            var pipeline = new ProcessingPipeline { PoolSize = pool, StripHeight = strip, Params = DefaultParams };
            var task = pipeline.RunAsync(src, new Progress<PipelineProgress>(_ => { }), cts.Token);
            if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(20))) != task)
            {
                hangs++;
                Console.WriteLine($"Зависание: pool={pool}, strip={strip}");
                continue;
            }
            if (task.IsCanceled || task.Exception?.GetBaseException() is OperationCanceledException) cancelled++;
            else if (task.IsCompletedSuccessfully)
            {
                completed++;
                if (!task.Result.Pixels.AsSpan().SequenceEqual(reference)) mismatches++;
            }
            else throw task.Exception!;
            if (FixedThreadPool.AliveThreads != 0) leaks++;
        }

        // Отмена записи: файл не должен появиться (ни целевой, ни временный).
        string outPath = Path.Combine(Path.GetTempPath(), "usm_cancel_test.png");
        File.Delete(outPath);
        int partialFiles = 0;
        for (int i = 0; i < 20; i++)
        {
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(rnd.Next(0, 30));
            try { await ImageIo.WriteAsync(src, outPath, cts.Token); File.Delete(outPath); }
            catch (OperationCanceledException)
            {
                if (File.Exists(outPath) || File.Exists(outPath + ".part")) partialFiles++;
            }
        }

        Console.WriteLine($"Итераций: {iterations} за {total.Elapsed.TotalSeconds:F1} с; завершено: {completed}, отменено: {cancelled}, " +
                          $"расхождений с эталоном: {mismatches}, зависаний: {hangs}, утечек потоков: {leaks}, " +
                          $"частичных файлов после отмены записи: {partialFiles}");
        return mismatches + hangs + leaks + partialFiles == 0 ? 0 : 1;
    }
}
