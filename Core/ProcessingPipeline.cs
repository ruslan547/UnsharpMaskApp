namespace UnsharpMaskApp.Core;

/// <summary>Задание на обработку - горизонтальная полоса изображения [Y0, Y1).</summary>
public sealed record StripJob(int Index, int Y0, int Y1);

/// <summary>Обработанный фрагмент, ожидающий сборки.</summary>
public sealed record Fragment(StripJob Job, byte[] Data, string WorkerName);

/// <summary>
/// Общие данные конвейера: очередь результатов, итоговый буфер, счётчик прогресса и журнал.
/// Любой доступ к ним выполняется только внутри критической секции lock (_sync).
/// </summary>
public sealed class ResultCollector
{
    private readonly object _sync = new();
    private readonly Queue<Fragment> _results = new();
    private readonly ImageBuffer _output;
    private readonly Dictionary<string, int> _perWorker = new();
    private readonly List<string> _journal = new();
    private int _assembled;
    private Exception? _error;

    public int Total { get; }

    public ResultCollector(ImageBuffer output, int total)
    {
        _output = output;
        Total = total;
    }

    /// <summary>Вызывается рабочим потоком: помещает фрагмент в очередь результатов.</summary>
    public void Add(Fragment fragment)
    {
        lock (_sync) // ---- критическая секция: очередь результатов ----
        {
            _results.Enqueue(fragment);
        }
    }

    /// <summary>Вызывается сборщиком: забирает все накопившиеся фрагменты.</summary>
    public List<Fragment> TakeAll()
    {
        lock (_sync) // ---- критическая секция: очередь результатов ----
        {
            var list = new List<Fragment>(_results);
            _results.Clear();
            return list;
        }
    }

    /// <summary>Копирует фрагмент в итоговый буфер и обновляет счётчик прогресса и журнал.</summary>
    public (int done, string message) Assemble(Fragment f)
    {
        lock (_sync) // ---- критическая секция: общий буфер, прогресс, журнал ----
        {
            Buffer.BlockCopy(f.Data, 0, _output.Pixels, f.Job.Y0 * _output.Stride, f.Data.Length);
            _assembled++;
            _perWorker[f.WorkerName] = _perWorker.GetValueOrDefault(f.WorkerName) + 1;
            string msg = $"[{_assembled}/{Total}] полоса #{f.Job.Index} (строки {f.Job.Y0}-{f.Job.Y1 - 1}) - {f.WorkerName}";
            _journal.Add(msg);
            return (_assembled, msg);
        }
    }

    public void SetError(Exception ex)
    {
        lock (_sync) { _error ??= ex; }
    }

    public Exception? Error { get { lock (_sync) return _error; } }
    public int Assembled { get { lock (_sync) return _assembled; } }

    public string WorkerStats()
    {
        lock (_sync)
        {
            return string.Join(", ", _perWorker.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => $"{p.Key}: {p.Value}"));
        }
    }
}

public sealed record PipelineProgress(int Done, int Total, string Message);

/// <summary>
/// Конвейер: планировщик -> очередь заданий -> пул потоков -> очередь результатов -> сборщик.
/// Синхронизация:
///  - lock/Monitor (критические секции) - очередь заданий пула, очередь результатов, буфер, прогресс, журнал;
///  - Monitor.Wait/Pulse (условная переменная) - ожидание заданий потоками пула;
///  - SemaphoreSlim - ограничение числа одновременно обрабатываемых фрагментов;
///  - AutoResetEvent - сигнал сборщику «появился результат»;
///  - CountdownEvent - ожидание сборки всех фрагментов;
///  - CancellationToken.WaitHandle (ManualResetEvent) - сигнал отмены.
/// </summary>
public sealed class ProcessingPipeline
{
    public int PoolSize { get; init; } = Environment.ProcessorCount;
    public int StripHeight { get; init; } = 64;
    public required UnsharpParams Params { get; init; }

    public string LastWorkerStats { get; private set; } = "";

    /// <summary>Запускает обработку вне UI-потока.</summary>
    public Task<ImageBuffer> RunAsync(ImageBuffer src, IProgress<PipelineProgress>? progress, CancellationToken token)
        => Task.Run(() => Run(src, progress, token), CancellationToken.None);

    public ImageBuffer Run(ImageBuffer src, IProgress<PipelineProgress>? progress, CancellationToken token)
    {
        var jobs = new List<StripJob>();
        for (int y = 0, i = 0; y < src.Height; y += StripHeight, i++)
            jobs.Add(new StripJob(i, y, Math.Min(src.Height, y + StripHeight)));

        float[] kernel = UnsharpMask.BuildKernel(Params.Sigma);
        var output = new ImageBuffer(src.Width, src.Height);
        var collector = new ResultCollector(output, jobs.Count);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token); // отмена пользователем или ошибка
        var ct = cts.Token;
        using var slots = new SemaphoreSlim(PoolSize, PoolSize);
        using var remaining = new CountdownEvent(jobs.Count);
        using var resultReady = new AutoResetEvent(false);

        // Сборщик результатов - отдельный поток, просыпается по событию resultReady.
        var collectorThread = new Thread(() =>
        {
            var handles = new[] { resultReady, ct.WaitHandle };
            while (!remaining.IsSet)
            {
                if (WaitHandle.WaitAny(handles) == 1) return; // отмена
                foreach (var f in collector.TakeAll())
                {
                    var (done, msg) = collector.Assemble(f);
                    remaining.Signal();
                    progress?.Report(new PipelineProgress(done, jobs.Count, msg));
                }
            }
        }) { Name = "Collector", IsBackground = true };
        collectorThread.Start();

        try
        {
            using var pool = new FixedThreadPool(PoolSize);
            try
            {
                // Планировщик: помещает задания в очередь пула, не более PoolSize одновременно.
                foreach (var job in jobs)
                {
                    slots.Wait(ct);
                    pool.Enqueue(() =>
                    {
                        try
                        {
                            if (ct.IsCancellationRequested) return;
                            byte[] data = UnsharpMask.ProcessStrip(src, job.Y0, job.Y1, Params, kernel, ct);
                            collector.Add(new Fragment(job, data, Thread.CurrentThread.Name ?? "?"));
                            resultReady.Set();
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex)
                        {
                            collector.SetError(ex);
                            cts.Cancel();
                        }
                        finally
                        {
                            slots.Release();
                        }
                    });
                }
                // Ожидание: либо собраны все фрагменты, либо отмена.
                WaitHandle.WaitAny(new[] { remaining.WaitHandle, ct.WaitHandle });
            }
            catch (OperationCanceledException) { }
        } // pool.Dispose(): все рабочие потоки завершены и присоединены
        finally
        {
            if (!remaining.IsSet) cts.Cancel();
            collectorThread.Join();
        }

        LastWorkerStats = collector.WorkerStats();
        if (collector.Error is { } error) throw new AggregateException("Ошибка обработки фрагмента", error);
        if (!remaining.IsSet) throw new OperationCanceledException(token);
        return output;
    }
}
