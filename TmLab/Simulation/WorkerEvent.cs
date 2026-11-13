namespace TmLab.Simulation;

/// <summary>
/// Что произошло в потоке. Событие кладётся в очередь, а разбирается
/// в UI-потоке — так рисование не тормозит расчёты и не портит состояние.
/// </summary>
public enum WorkerEventKind
{
    /// <summary>Поток начал новую операцию (зачисление или перевод).</summary>
    OpBegin,

    /// <summary>Чтение счёта.</summary>
    Read,

    /// <summary>Запись в локальный журнал транзакции.</summary>
    Write,

    /// <summary>Валидация версий.</summary>
    Validate,

    /// <summary>Транзакция зафиксирована (в Примере 1 — успешный CompareExchange).</summary>
    Commit,

    /// <summary>Конфликт: транзакция откатилась и будет переиграна.</summary>
    Conflict,

    /// <summary>Отказ по бизнес-причине (не хватает средств или превышен потолок).</summary>
    Refused,

    /// <summary>Счёт захвачен (режим блокировок).</summary>
    Lock,

    /// <summary>Поток ждёт освобождения счёта (режим блокировок).</summary>
    Wait,

    /// <summary>Блокировка снята.</summary>
    Unlock,

    /// <summary>Пауза между операциями.</summary>
    Idle
}

/// <summary>Причина отказа операции (для событий вида Refused).</summary>
public enum RefusalReason
{
    /// <summary>Отказа не было.</summary>
    None,

    /// <summary>На счёте отправителя не хватает средств для перевода.</summary>
    InsufficientFunds,

    /// <summary>Сумма перевода или пополнения превышает разрешённый потолок.</summary>
    OverLimit,

    /// <summary>Пополнение вывело бы баланс счёта за лимит.</summary>
    OverBalanceCap
}

/// <summary>Одно событие потока: неизменяемая структура, безопасна для передачи между потоками.</summary>
public readonly struct WorkerEvent
{
    public WorkerEvent(WorkerEventKind kind, int workerId, int from, int to, int amount, int attempt, long timeMs,
        RefusalReason reason = RefusalReason.None)
    {
        Kind = kind;
        WorkerId = workerId;
        From = from;
        To = to;
        Amount = amount;
        Attempt = attempt;
        TimeMs = timeMs;
        Reason = reason;
    }

    public WorkerEventKind Kind { get; }

    /// <summary>Номер потока (0…Workers-1) — по нему нарисован кружок на схеме.</summary>
    public int WorkerId { get; }

    /// <summary>Счёт-отправитель (для зачисления совпадает с получателем).</summary>
    public int From { get; }

    /// <summary>Счёт-получатель.</summary>
    public int To { get; }

    public int Amount { get; }

    /// <summary>Номер попытки транзакции.</summary>
    public int Attempt { get; }

    /// <summary>Время от старта демонстрации, мс — для анимации и таймлайна.</summary>
    public long TimeMs { get; }

    /// <summary>Причина отказа (только для событий вида Refused).</summary>
    public RefusalReason Reason { get; }
}
