using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using TmLab.Models;

namespace TmLab.Simulation;

/// <summary>
/// Движок демонстрации: запускает настоящие параллельные потоки и складывает
/// их события в очередь, которую разбирает интерфейс.
///
/// Здесь реализованы все четыре сценария из работы:
///   * Cas    — зачисление циклом CompareExchange (Пример 1 из задания);
///   * Stm    — перевод между счетами внутри транзакции (Пример 2);
///   * Lock   — тот же перевод, но на пессимистичных блокировках;
///   * Unsafe — без синхронизации, чтобы показать потерянные обновления.
/// </summary>
public sealed class SimulationRunner
{
    private readonly ConcurrentQueue<WorkerEvent> _events = new();
    private readonly Stopwatch _clock = new();

    private CancellationTokenSource? _cts;
    private int _operations;
    private int _commits;
    private int _retries;
    private int _refusals;
    private int _waits;

    public SimulationRunner(SimulationOptions options)
    {
        Options = options;
        Bank = new Bank(options.Accounts, options.Total);
    }

    public SimulationOptions Options { get; }

    public Bank Bank { get; }

    public bool IsRunning => _cts is { IsCancellationRequested: false };

    /// <summary>
    /// Пауза: потоки остаются живыми, но новых операций не начинают —
    /// состояние банка при этом сохраняется.
    /// </summary>
    public bool Paused { get; set; }

    public int Operations => Volatile.Read(ref _operations);

    public int Commits => Volatile.Read(ref _commits);

    /// <summary>Сколько раз транзакция (или CAS-цикл) откатилась из-за конфликта.</summary>
    public int Retries => Volatile.Read(ref _retries);

    public int Refusals => Volatile.Read(ref _refusals);

    /// <summary>Сколько раз поток ждал освобождения счёта.</summary>
    public int Waits => Volatile.Read(ref _waits);

    public bool TryDequeueEvent(out WorkerEvent workerEvent) => _events.TryDequeue(out workerEvent);

    // -------------------------------------------------------------------------
    // Запуск и остановка
    // -------------------------------------------------------------------------

    public void Start()
    {
        Stop();

        _operations = _commits = _retries = _refusals = _waits = 0;
        while (_events.TryDequeue(out _)) { }

        _clock.Restart();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        // Отдельные длительные задачи: потоки будут честно выполнять SpinWait,
        // а не отдавать квант планировщику пула.
        for (int i = 0; i < Options.Workers; i++)
        {
            int workerId = i;
            Task.Factory.StartNew(
                () => RunWorker(workerId, token),
                token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
    }

    public void Stop()
    {
        var source = _cts;
        _cts = null;
        source?.Cancel();
        source?.Dispose();
        _clock.Stop();
    }

    /// <summary>Полная остановка: используется при сбросе демонстрации.</summary>
    public void Reset()
    {
        Stop();
        _operations = _commits = _retries = _refusals = _waits = 0;
        while (_events.TryDequeue(out _)) { }
    }

    private void Emit(WorkerEventKind kind, int workerId, int from, int to, int amount, int attempt = 1)
    {
        _events.Enqueue(new WorkerEvent(kind, workerId, from, to, amount, attempt, _clock.ElapsedMilliseconds));
    }

    // -------------------------------------------------------------------------
    // Рабочий цикл потока
    // -------------------------------------------------------------------------

    private void RunWorker(int workerId, CancellationToken token)
    {
        // Свой генератор случайных чисел: Random не потокобезопасен.
        var random = new Random(workerId * 7919 + 13);

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (Paused)
                {
                    if (token.WaitHandle.WaitOne(50))
                        break;
                    continue;
                }

                if (Options.Demo == 1)
                    DepositOnce(workerId, token);
                else
                    TransferOnce(workerId, random, token);

                if (Options.GapMs > 0)
                {
                    if (token.WaitHandle.WaitOne(Options.GapMs))
                        break;                     // остановка во время паузы
                    Emit(WorkerEventKind.Idle, workerId, 0, 0, 0);
                }
                else
                {
                    Thread.Yield();
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // токен освобождён при остановке — это нормальное завершение потока
        }
    }

    /// <summary>
    /// «Тяжёлая работа» внутри операции: заданное число микросекунд занятого
    /// ожидания (аналог Thread.SpinWait из задания). Из-за этой задержки
    /// операция держит прочитанные значения «устаревшими», и транзакции
    /// конфликтуют тем чаще, чем она длиннее.
    /// </summary>
    private void HeavyWork()
    {
        int microseconds = Options.WorkMicroseconds;
        if (Options.AggressiveConflicts)
            microseconds = (int)(microseconds * (1.0 + Random.Shared.NextDouble() * 2.0));

        if (microseconds <= 0)
            return;

        long start = Stopwatch.GetTimestamp();
        long ticks = (long)(microseconds * (double)Stopwatch.Frequency / 1_000_000.0);

        while (Stopwatch.GetTimestamp() - start < ticks)
            Thread.SpinWait(32);
    }

    private void CountOperation(WorkerEventKind kind, int workerId, int from, int to, int amount, int attempt)
    {
        Interlocked.Increment(ref _operations);
        if (kind == WorkerEventKind.Commit)
            Interlocked.Increment(ref _commits);
        else
            Interlocked.Increment(ref _refusals);

        Emit(kind, workerId, from, to, amount, attempt);
    }

    // -------------------------------------------------------------------------
    // Пример 1: зачисление на счёт
    // -------------------------------------------------------------------------

    private void DepositOnce(int workerId, CancellationToken token)
    {
        var account = Bank.Accounts[0];
        int amount = Options.Amount;

        Emit(WorkerEventKind.OpBegin, workerId, 0, 0, amount);

        switch (Options.Technique)
        {
            case Technique.Unsafe:
            {
                // Читаем, «думаем», записываем — без всякой защиты.
                // Именно поэтому часть пополнений теряется.
                int current = account.Balance;
                Emit(WorkerEventKind.Read, workerId, 0, 0, amount);
                HeavyWork();
                account.SetBalanceUnsafe(current + amount);
                CountOperation(WorkerEventKind.Commit, workerId, 0, 0, amount, 1);
                break;
            }

            case Technique.Lock:
            {
                if (!Acquire(0, workerId, token))
                    return;

                try
                {
                    int current = account.Balance;
                    HeavyWork();
                    account.SetBalance(current + amount);
                    Emit(WorkerEventKind.Write, workerId, 0, 0, current + amount);
                }
                finally
                {
                    account.Unlock();
                    Emit(WorkerEventKind.Unlock, workerId, 0, 0, 0);
                }

                CountOperation(WorkerEventKind.Commit, workerId, 0, 0, amount, 1);
                break;
            }

            default:
            {
                // Цикл сравнения с обменом: do { ... } while (CAS не прошёл).
                while (!token.IsCancellationRequested)
                {
                    int current = account.Balance;

                    // чтение попадает и в таймлайн, и в журнал
                    Emit(WorkerEventKind.Read, workerId, 0, 0, amount);

                    int next = current + amount;
                    HeavyWork();

                    if (account.TryCompareExchange(current, next))
                    {
                        CountOperation(WorkerEventKind.Commit, workerId, 0, 0, amount, 1);
                        return;
                    }

                    // Другой поток успел изменить баланс: попытка отброшена.
                    Interlocked.Increment(ref _retries);
                    Emit(WorkerEventKind.Conflict, workerId, 0, 0, amount, 1);
                }

                break;
            }
        }
    }

    // -------------------------------------------------------------------------
    // Пример 2: перевод между счетами
    // -------------------------------------------------------------------------

    private void TransferOnce(int workerId, Random random, CancellationToken token)
    {
        int count = Bank.Accounts.Length;
        int from = random.Next(count);
        int to = random.Next(count);

        if (from == to)
            to = (to + 1) % count;

        int amount = 1 + random.Next(Options.MaxTransfer);

        Emit(WorkerEventKind.OpBegin, workerId, from, to, amount);

        switch (Options.Technique)
        {
            case Technique.Stm:
                TransferStm(workerId, from, to, amount, token);
                break;

            case Technique.Lock:
                TransferLock(workerId, from, to, amount, token);
                break;

            default:
                TransferUnsafe(workerId, from, to, amount, token);
                break;
        }
    }

    /// <summary>Перевод целиком внутри транзакции: журнал → валидация → фиксация.</summary>
    private void TransferStm(int workerId, int from, int to, int amount, CancellationToken token)
    {
        var accounts = Bank.Accounts;

        void Body(TxContext context)
        {
            Emit(WorkerEventKind.Read, workerId, from, to, amount);

            int balanceFrom = context.Read(accounts[from]);
            int balanceTo = context.Read(accounts[to]);

            if (balanceFrom < amount)
                throw new InsufficientFundsException();

            Emit(WorkerEventKind.Write, workerId, from, to, amount);
            HeavyWork();

            context.Write(accounts[from], balanceFrom - amount);
            context.Write(accounts[to], balanceTo + amount);
        }

        var result = TxContext.Execute(
            Body,
            token,
            onConflict: attempt =>
            {
                Interlocked.Increment(ref _retries);
                Emit(WorkerEventKind.Conflict, workerId, from, to, amount, attempt);
            },
            onValidated: (valid, attempt) =>
                Emit(WorkerEventKind.Validate, workerId, from, to, amount, attempt));

        switch (result.Outcome)
        {
            case TxOutcome.Committed:
                CountOperation(WorkerEventKind.Commit, workerId, from, to, amount, result.Attempts);
                break;

            case TxOutcome.Refused:
                CountOperation(WorkerEventKind.Refused, workerId, from, to, amount, result.Attempts);
                break;

            case TxOutcome.Livelock:
                Emit(WorkerEventKind.Conflict, workerId, from, to, amount, result.Attempts);
                break;
        }
    }

    /// <summary>
    /// Перевод на блокировках. Счета захватываются в порядке возрастания номера —
    /// это исключает взаимную блокировку (deadlock), о которой предупреждает лекция.
    /// </summary>
    private void TransferLock(int workerId, int from, int to, int amount, CancellationToken token)
    {
        int first = Math.Min(from, to);
        int second = Math.Max(from, to);

        if (!Acquire(first, workerId, token))
            return;

        bool secondLocked = false;

        try
        {
            if (second != first)
            {
                if (!Acquire(second, workerId, token))
                    return;

                secondLocked = true;
            }

            var accounts = Bank.Accounts;
            Emit(WorkerEventKind.Read, workerId, from, to, amount);

            int balanceFrom = accounts[from].Balance;
            int balanceTo = accounts[to].Balance;

            HeavyWork();

            if (balanceFrom < amount)
            {
                CountOperation(WorkerEventKind.Refused, workerId, from, to, amount, 1);
                return;
            }

            Emit(WorkerEventKind.Write, workerId, from, to, amount);
            accounts[from].SetBalance(balanceFrom - amount);
            accounts[to].SetBalance(balanceTo + amount);

            CountOperation(WorkerEventKind.Commit, workerId, from, to, amount, 1);
        }
        finally
        {
            if (secondLocked)
                Bank.Accounts[second].Unlock();

            Bank.Accounts[first].Unlock();
            Emit(WorkerEventKind.Unlock, workerId, first, second, 0);
        }
    }

    /// <summary>Перевод без синхронизации: сумма на счетах «плывёт».</summary>
    private void TransferUnsafe(int workerId, int from, int to, int amount, CancellationToken token)
    {
        var accounts = Bank.Accounts;

        Emit(WorkerEventKind.Read, workerId, from, to, amount);
        int balanceFrom = accounts[from].Balance;
        int balanceTo = accounts[to].Balance;

        HeavyWork();

        if (balanceFrom < amount)
        {
            CountOperation(WorkerEventKind.Refused, workerId, from, to, amount, 1);
            return;
        }

        accounts[from].SetBalanceUnsafe(balanceFrom - amount);
        accounts[to].SetBalanceUnsafe(balanceTo + amount);

        CountOperation(WorkerEventKind.Commit, workerId, from, to, amount, 1);
    }

    // -------------------------------------------------------------------------
    // Захват блокировки
    // -------------------------------------------------------------------------

    /// <summary>
    /// Захватить счёт в режиме блокировок.
    ///
    /// Сам захват выполняется под общим замком фиксации: иначе возможна гонка,
    /// когда транзакция провалидируется, а «пессимистичный» поток сразу после
    /// этого прочитает устаревшее значение и затрёт результат фиксации.
    /// Взаимная блокировка исключена порядком захвата (по возрастанию номера).
    /// </summary>
    private bool Acquire(int accountIndex, int workerId, CancellationToken token)
    {
        var account = Bank.Accounts[accountIndex];
        bool counted = false;      // ожидание считаем один раз на одну попытку захвата

        while (!token.IsCancellationRequested)
        {
            lock (TxContext.CommitGate)
            {
                if (!account.IsLocked)
                {
                    account.TryLock();
                    Emit(WorkerEventKind.Lock, workerId, accountIndex, accountIndex, 0, 1);
                    return true;
                }
            }

            if (!counted)
            {
                counted = true;
                Interlocked.Increment(ref _waits);
            }

            Emit(WorkerEventKind.Wait, workerId, accountIndex, accountIndex, 0, 1);

            if (token.WaitHandle.WaitOne(2))
                break;
        }

        return false;
    }
}
