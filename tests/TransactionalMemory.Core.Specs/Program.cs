using TransactionalMemory.Core;

var specs = new (string Name, Action Run)[]
{
    ("successful transfer changes both balances atomically", SuccessfulTransfer),
    ("insufficient funds leave both accounts untouched", InsufficientFunds),
    ("parallel transfers preserve the total balance", ParallelTransfers),
    ("version conflict retries the transaction body", ConflictingTransactionRetries)
};

var failed = 0;
foreach (var (name, run) in specs)
{
    try
    {
        run();
        Console.WriteLine($"PASS  {name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL  {name}: {exception.Message}");
    }
}

Console.WriteLine($"{specs.Length - failed}/{specs.Length} specifications passed.");
return failed == 0 ? 0 : 1;

static void SuccessfulTransfer()
{
    var demo = new BankDemo();
    var result = demo.TryTransfer(demo.AccountA, demo.AccountB, 125m);
    var snapshot = demo.ReadSnapshot();

    True(result.Succeeded, "A valid transfer should succeed.");
    Equal(875m, snapshot.BalanceA, "Unexpected source balance.");
    Equal(1_125m, snapshot.BalanceB, "Unexpected destination balance.");
    Equal(2_000m, snapshot.Total, "The transfer must preserve the total.");
    Equal(1L, snapshot.VersionA, "The source version should advance once.");
    Equal(1L, snapshot.VersionB, "The destination version should advance once.");
}

static void InsufficientFunds()
{
    var demo = new BankDemo();
    var result = demo.TryTransfer(demo.AccountA, demo.AccountB, 1_001m);
    var snapshot = demo.ReadSnapshot();

    True(!result.Succeeded, "A transfer larger than the balance must be rejected.");
    Equal(1_000m, snapshot.BalanceA, "Rejected transfer changed the source.");
    Equal(1_000m, snapshot.BalanceB, "Rejected transfer changed the destination.");
    Equal(0L, snapshot.VersionA, "Rejected transfer should not write the source cell.");
    Equal(0L, snapshot.VersionB, "Rejected transfer should not write the destination cell.");
}

static void ParallelTransfers()
{
    var demo = new BankDemo();
    var summary = demo.RunParallelTest(10m, 500);
    var snapshot = demo.ReadSnapshot();

    Equal(500, summary.Requests, "Unexpected request count.");
    Equal(500, summary.CompletedTransfers + summary.RejectedTransfers, "Every request should have a result.");
    True(summary.Attempts >= summary.Requests, "Each request needs at least one attempt.");
    Equal(summary.Attempts - summary.Requests, summary.Conflicts, "Retry statistics are inconsistent.");
    True(snapshot.BalanceA >= 0m && snapshot.BalanceB >= 0m, "A balance became negative.");
    Equal(2_000m, snapshot.Total, "Concurrent operations created or lost money.");
}

static void ConflictingTransactionRetries()
{
    var engine = new StmEngine();
    var cell = engine.CreateCell(0);
    using var firstAttemptRead = new ManualResetEventSlim();
    using var allowFirstAttemptToCommit = new ManualResetEventSlim();
    var bodyExecutions = 0;

    var worker = Task.Run(() => engine.Execute(transaction =>
    {
        var value = transaction.Read(cell);
        if (Interlocked.Increment(ref bodyExecutions) == 1)
        {
            firstAttemptRead.Set();
            if (!allowFirstAttemptToCommit.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The test did not release the first transaction attempt.");
            }
        }

        transaction.Write(cell, value + 1);
        return value;
    }));

    if (!firstAttemptRead.Wait(TimeSpan.FromSeconds(5)))
    {
        allowFirstAttemptToCommit.Set();
        throw new TimeoutException("The worker did not start its first transaction attempt.");
    }

    engine.Execute(transaction =>
    {
        transaction.Write(cell, transaction.Read(cell) + 10);
        return true;
    });
    allowFirstAttemptToCommit.Set();

    var result = worker.GetAwaiter().GetResult();
    Equal(2, result.Attempts, "The stale attempt should be discarded and re-run.");
    Equal(2, bodyExecutions, "The transaction body should run again exactly once.");
    Equal(11, cell.ReadSnapshot().Value, "The retried write should use the new committed value.");
    True(engine.ConflictCount >= 1, "The engine should record the validation conflict.");
}

static void True(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message} Expected: {expected}; actual: {actual}.");
    }
}
