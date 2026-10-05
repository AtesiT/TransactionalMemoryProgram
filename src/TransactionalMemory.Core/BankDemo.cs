using System.Threading;

namespace TransactionalMemory.Core;

public sealed class BankAccount
{
    internal BankAccount(string name, TransactionalCell<decimal> balance)
    {
        Name = name;
        Balance = balance;
    }

    public string Name { get; }
    internal TransactionalCell<decimal> Balance { get; }
}

public sealed class BankDemo
{
    public const decimal InitialBalance = 1_000m;
    private const int MaxAttempts = 4_096;
    private readonly StmEngine _memory = new();

    public BankDemo()
    {
        AccountA = new BankAccount("A", _memory.CreateCell(InitialBalance));
        AccountB = new BankAccount("B", _memory.CreateCell(InitialBalance));
    }

    public BankAccount AccountA { get; }
    public BankAccount AccountB { get; }

    /// <summary>
    /// Reads both accounts in one validated read-only transaction, so the displayed total is coherent.
    /// </summary>
    public BankSnapshot ReadSnapshot()
    {
        var snapshot = _memory.Execute(transaction =>
        {
            var a = transaction.ReadSnapshot(AccountA.Balance);
            var b = transaction.ReadSnapshot(AccountB.Balance);
            return new BankSnapshot(a.Value, a.Version, b.Value, b.Version);
        }, MaxAttempts);

        return snapshot.Value;
    }

    /// <summary>
    /// Attempts an atomic transfer. A rejected transfer commits no writes.
    /// </summary>
    public TransferReceipt TryTransfer(
        BankAccount from,
        BankAccount to,
        decimal amount,
        bool yieldToIncreaseContention = false)
    {
        ValidateTransfer(from, to, amount);

        var result = _memory.Execute(transaction =>
        {
            var source = transaction.Read(from.Balance);
            var destination = transaction.Read(to.Balance);

            // The stress demo briefly gives competing workers a chance to read the same versions.
            // It makes retries visible without placing delays inside the STM engine itself.
            if (yieldToIncreaseContention)
            {
                Thread.Yield();
            }

            if (source < amount)
            {
                return false;
            }

            transaction.Write(from.Balance, source - amount);
            transaction.Write(to.Balance, destination + amount);
            return true;
        }, MaxAttempts);

        return new TransferReceipt(result.Value, result.Attempts);
    }

    /// <summary>Runs competing transfers in parallel and returns retry/commit statistics.</summary>
    public ParallelTestSummary RunParallelTest(decimal amount, int requestCount)
    {
        if (amount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Сумма должна быть больше нуля.");
        }

        if (requestCount is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(requestCount), "Количество операций должно быть от 1 до 10 000.");
        }

        long attempts = 0;
        var completedTransfers = 0;
        var rejectedTransfers = 0;
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8)
        };

        Parallel.For(0, requestCount, parallelOptions, index =>
        {
            var from = index % 2 == 0 ? AccountA : AccountB;
            var to = index % 2 == 0 ? AccountB : AccountA;
            var receipt = TryTransfer(from, to, amount, yieldToIncreaseContention: true);

            Interlocked.Add(ref attempts, receipt.Attempts);
            if (receipt.Succeeded)
            {
                Interlocked.Increment(ref completedTransfers);
            }
            else
            {
                Interlocked.Increment(ref rejectedTransfers);
            }
        });

        var totalAttempts = checked((int)attempts);
        return new ParallelTestSummary(
            requestCount,
            completedTransfers,
            rejectedTransfers,
            totalAttempts,
            totalAttempts - requestCount);
    }

    private void ValidateTransfer(BankAccount from, BankAccount to, decimal amount)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        var sourceBelongsToDemo = ReferenceEquals(from, AccountA) || ReferenceEquals(from, AccountB);
        var destinationBelongsToDemo = ReferenceEquals(to, AccountA) || ReferenceEquals(to, AccountB);
        if (!sourceBelongsToDemo || !destinationBelongsToDemo)
        {
            throw new ArgumentException("Счета должны принадлежать этому банковскому примеру.");
        }

        if (ReferenceEquals(from, to))
        {
            throw new ArgumentException("Счета отправителя и получателя должны различаться.");
        }

        if (amount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Сумма должна быть больше нуля.");
        }
    }
}

public readonly record struct BankSnapshot(
    decimal BalanceA,
    long VersionA,
    decimal BalanceB,
    long VersionB)
{
    public decimal Total => BalanceA + BalanceB;
}

public readonly record struct TransferReceipt(bool Succeeded, int Attempts)
{
    public int Conflicts => Attempts - 1;
}

public readonly record struct ParallelTestSummary(
    int Requests,
    int CompletedTransfers,
    int RejectedTransfers,
    int Attempts,
    int Conflicts);
