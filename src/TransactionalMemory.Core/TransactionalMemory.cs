using System.Threading;

namespace TransactionalMemory.Core;

/// <summary>
/// A small, educational software transactional memory (STM) engine.
/// Transactions keep private read/write sets and publish all writes together only after
/// the versions they observed have been validated.
/// </summary>
public sealed class StmEngine
{
    private readonly object _commitGate = new();
    private long _conflictCount;

    public long ConflictCount => Interlocked.Read(ref _conflictCount);

    public TransactionalCell<T> CreateCell<T>(T initialValue) => new(this, initialValue);

    /// <summary>
    /// Runs <paramref name="body"/> and retries it if a value in its read set changed before commit.
    /// The body may run more than once: do not perform I/O or other external side effects in it.
    /// </summary>
    public TransactionResult<TResult> Execute<TResult>(
        Func<TransactionContext, TResult> body,
        int maxAttempts = 1024,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), "Число попыток должно быть не меньше единицы.");
        }

        var backoff = new SpinWait();
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var transaction = new TransactionContext(this);

            try
            {
                var result = body(transaction);
                cancellationToken.ThrowIfCancellationRequested();

                if (TryCommit(transaction))
                {
                    return new TransactionResult<TResult>(result, attempt);
                }

                Interlocked.Increment(ref _conflictCount);
            }
            finally
            {
                transaction.Close();
            }

            if (attempt < maxAttempts)
            {
                backoff.SpinOnce();
            }
        }

        throw new StmConflictException(maxAttempts);
    }

    internal CellSnapshotData Capture(TransactionalCellState state)
    {
        EnsureOwned(state);
        lock (_commitGate)
        {
            return new CellSnapshotData(state.Value, state.Version);
        }
    }

    internal CellSnapshot<T> ReadCommitted<T>(TransactionalCell<T> cell)
    {
        var state = cell.State;
        EnsureOwned(state);
        lock (_commitGate)
        {
            return new CellSnapshot<T>(Unbox<T>(state.Value), state.Version);
        }
    }

    private bool TryCommit(TransactionContext transaction)
    {
        lock (_commitGate)
        {
            foreach (var (state, observed) in transaction.ReadSet)
            {
                if (state.Version != observed.Version)
                {
                    return false;
                }
            }

            foreach (var (state, value) in transaction.WriteSet)
            {
                state.Value = value;
                state.Version++;
            }

            return true;
        }
    }

    private void EnsureOwned(TransactionalCellState state)
    {
        if (!ReferenceEquals(state.Engine, this))
        {
            throw new InvalidOperationException("Транзакционная ячейка принадлежит другому STM-движку.");
        }
    }

    private static T Unbox<T>(object? value) => value is null ? default! : (T)value;
}

/// <summary>A versioned value owned by one STM engine. Use immutable values inside a cell.</summary>
public sealed class TransactionalCell<T>
{
    internal TransactionalCell(StmEngine engine, T initialValue) => State = new TransactionalCellState(engine, initialValue);

    internal TransactionalCellState State { get; }

    /// <summary>Reads the last committed value and its version.</summary>
    public CellSnapshot<T> ReadSnapshot() => State.Engine.ReadCommitted(this);
}

/// <summary>A transaction-local view. Its writes remain private until the engine commits them.</summary>
public sealed class TransactionContext
{
    private readonly StmEngine _engine;
    private bool _isClosed;

    internal TransactionContext(StmEngine engine) => _engine = engine;

    internal Dictionary<TransactionalCellState, CellSnapshotData> ReadSet { get; } = new();
    internal Dictionary<TransactionalCellState, object?> WriteSet { get; } = new();

    public T Read<T>(TransactionalCell<T> cell) => ReadSnapshot(cell).Value;

    /// <summary>Reads a cell once and returns that transaction's stable value/version pair.</summary>
    public CellSnapshot<T> ReadSnapshot<T>(TransactionalCell<T> cell)
    {
        var state = cell.State;
        EnsureActiveAndOwned(state);
        var original = CaptureOnce(state);
        var value = WriteSet.TryGetValue(state, out var pendingWrite)
            ? Unbox<T>(pendingWrite)
            : Unbox<T>(original.Value);
        return new CellSnapshot<T>(value, original.Version);
    }

    public void Write<T>(TransactionalCell<T> cell, T value)
    {
        var state = cell.State;
        EnsureActiveAndOwned(state);
        _ = CaptureOnce(state); // A write must also validate the version it is based on.
        WriteSet[state] = value;
    }

    public void Update<T>(TransactionalCell<T> cell, Func<T, T> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        Write(cell, update(Read(cell)));
    }

    internal void Close() => _isClosed = true;

    private CellSnapshotData CaptureOnce(TransactionalCellState state)
    {
        if (ReadSet.TryGetValue(state, out var captured))
        {
            return captured;
        }

        captured = _engine.Capture(state);
        ReadSet.Add(state, captured);
        return captured;
    }

    private void EnsureActiveAndOwned(TransactionalCellState state)
    {
        if (_isClosed)
        {
            throw new InvalidOperationException("Эта транзакция уже завершена.");
        }

        if (!ReferenceEquals(state.Engine, _engine))
        {
            throw new InvalidOperationException("Нельзя смешивать ячейки разных STM-движков.");
        }
    }

    private static T Unbox<T>(object? value) => value is null ? default! : (T)value;
}

internal sealed class TransactionalCellState
{
    internal TransactionalCellState(StmEngine engine, object? initialValue)
    {
        Engine = engine;
        Value = initialValue;
    }

    internal StmEngine Engine { get; }
    internal object? Value { get; set; }
    internal long Version { get; set; }
}

internal readonly record struct CellSnapshotData(object? Value, long Version);

public readonly record struct CellSnapshot<T>(T Value, long Version);

public readonly record struct TransactionResult<TResult>(TResult Value, int Attempts)
{
    public int Conflicts => Attempts - 1;
}

public sealed class StmConflictException : InvalidOperationException
{
    public StmConflictException(int attempts)
        : base($"Транзакция не смогла зафиксироваться за {attempts} попыток.") => Attempts = attempts;

    public int Attempts { get; }
}
