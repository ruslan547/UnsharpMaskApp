namespace UnsharpMaskApp.Core;

/// <summary>
/// Пул потоков фиксированного размера с общей очередью заданий.
/// Очередь защищена критической секцией (Monitor), ожидание заданий - условная переменная Monitor.Wait/Pulse.
/// </summary>
public sealed class FixedThreadPool : IDisposable
{
    private readonly object _queueLock = new();
    private readonly Queue<Action> _jobs = new();
    private readonly Thread[] _workers;
    private bool _shutdown;
    private static int _aliveThreads;

    /// <summary>Количество живых рабочих потоков всех пулов (для проверки корректного завершения).</summary>
    public static int AliveThreads => Volatile.Read(ref _aliveThreads);

    public FixedThreadPool(int size, string name = "Worker")
    {
        if (size < 1) throw new ArgumentOutOfRangeException(nameof(size));
        _workers = new Thread[size];
        for (int i = 0; i < size; i++)
        {
            _workers[i] = new Thread(WorkerLoop) { IsBackground = true, Name = $"{name}-{i + 1}" };
            Interlocked.Increment(ref _aliveThreads);
            _workers[i].Start();
        }
    }

    public void Enqueue(Action job)
    {
        lock (_queueLock) // ---- критическая секция: очередь заданий ----
        {
            if (_shutdown) throw new InvalidOperationException("Пул остановлен");
            _jobs.Enqueue(job);
            Monitor.Pulse(_queueLock); // сигнал «появилось задание» одному ожидающему потоку
        }
    }

    private void WorkerLoop()
    {
        try
        {
            while (true)
            {
                Action job;
                lock (_queueLock) // ---- критическая секция: очередь заданий ----
                {
                    while (_jobs.Count == 0 && !_shutdown)
                        Monitor.Wait(_queueLock); // условная переменная: ждём задание или остановку
                    if (_jobs.Count == 0) return; // _shutdown и очередь пуста - выходим
                    job = _jobs.Dequeue();
                }
                job(); // выполняется вне блокировки
            }
        }
        finally
        {
            Interlocked.Decrement(ref _aliveThreads);
        }
    }

    /// <summary>Останавливает пул: оставшиеся задания дорабатываются (при отмене они завершаются сразу), потоки присоединяются.</summary>
    public void Dispose()
    {
        lock (_queueLock)
        {
            if (_shutdown) return;
            _shutdown = true;
            Monitor.PulseAll(_queueLock);
        }
        foreach (var t in _workers) t.Join();
    }
}
