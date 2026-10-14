namespace TmLab.Simulation;

/// <summary>Способ синхронизации потоков, который демонстрирует приложение.</summary>
public enum Technique
{
    /// <summary>Транзакционная память (оптимистичный подход) — Пример 2.</summary>
    Stm,

    /// <summary>Пессимистичные блокировки: счёт захватывается на время операции.</summary>
    Lock,

    /// <summary>Цикл сравнения с обменом — Пример 1 из задания.</summary>
    Cas,

    /// <summary>Без синхронизации: специально сломанный режим для сравнения.</summary>
    Unsafe
}

/// <summary>Параметры запуска демонстрации.</summary>
public sealed class SimulationOptions
{
    /// <summary>1 — зачисление на счёт (Пример 1), 2 — переводы между счетами (Пример 2).</summary>
    public int Demo { get; set; } = 1;

    public Technique Technique { get; set; } = Technique.Cas;

    public int Workers { get; set; } = 8;

    public int Accounts { get; set; } = 1;

    public int Total { get; set; } = 1000;

    /// <summary>Сумма зачисления в Примере 1.</summary>
    public int Amount { get; set; } = 10;

    /// <summary>Максимальная сумма перевода в Примере 2.</summary>
    public int MaxTransfer { get; set; } = 50;

    /// <summary>
    /// Пауза между операциями, мс: чем больше, тем спокойнее и нагляднее показ.
    /// Меняется на ходу.
    /// </summary>
    public int GapMs { get; set; } = 400;

    /// <summary>
    /// «Тяжёлая работа» внутри операции, микросекунды занятого ожидания —
    /// аналог Thread.SpinWait из задания. Чем дольше операция держит счёт
    /// прочитанным, тем чаще транзакции конфликтуют. Меняется на ходу.
    /// </summary>
    public int WorkMicroseconds { get; set; } = 600;

    /// <summary>Разброс времени операции: делает конфликты заметнее.</summary>
    public bool AggressiveConflicts { get; set; } = true;

    // --- готовые темпы показа ----------------------------------------------

    /// <summary>Медленно: каждый перевод видно по отдельности.</summary>
    public static SimulationOptions Show => new() { GapMs = 1000, WorkMicroseconds = 1500 };

    /// <summary>Обычный темп для наблюдения.</summary>
    public static SimulationOptions Normal => new() { GapMs = 400, WorkMicroseconds = 600 };

    /// <summary>Много операций в секунду: видно конкуренцию и всплеск отказов.</summary>
    public static SimulationOptions Load => new() { GapMs = 25, WorkMicroseconds = 300 };
}
