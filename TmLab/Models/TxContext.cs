using System;
using System.Collections.Generic;
using System.Threading;

namespace TmLab.Models;

/// <summary>Отказ по бизнес-логике (например, на счёте не хватает денег).</summary>
public sealed class InsufficientFundsException : Exception
{
    public InsufficientFundsException() : base("Недостаточно средств") { }
}

/// <summary>Чем закончилась попытка транзакции.</summary>
public enum TxOutcome
{
    /// <summary>Изменения зафиксированы в общей памяти.</summary>
    Committed,

    /// <summary>Транзакция отменена по бизнес-причине (не хватило средств).</summary>
    Refused,

    /// <summary>Слишком много конфликтов подряд — предохранитель от «живого» ожидания.</summary>
    Livelock,

    /// <summary>Работа остановлена пользователем.</summary>
    Cancelled
}

/// <summary>Результат выполнения транзакции.</summary>
public readonly struct TxResult
{
    public TxResult(TxOutcome outcome, int attempts)
    {
        Outcome = outcome;
        Attempts = attempts;
    }

    public TxOutcome Outcome { get; }

    /// <summary>Номер попытки, на которой транзакция завершилась.</summary>
    public int Attempts { get; }
}

/// <summary>
/// Контекст транзакции: локальный журнал чтения/записи, валидация версий,
/// фиксация или откат с повтором.
///
/// Это «своя мини-STM» — то, что в задании предлагается сделать вместо
/// отсутствующей в .NET библиотеки транзакционной памяти.
/// </summary>
public sealed class TxContext
{
    [ThreadStatic] private static TxContext? _current;

    /// <summary>Журнал текущего потока. Вне транзакции равно null.</summary>
    public static TxContext? Current => _current;

    /// <summary>
    /// Общий замок фиксации. Валидация и запись обязаны быть неделимы: иначе два
    /// потока успеют провалидироваться на одной версии данных и оба запишут
    /// устаревшие значения — инвариант банка сломается. Так же поступают
    /// настоящие STM-библиотеки (например, TL2 блокирует записываемые ячейки).
    /// </summary>
    public static readonly object CommitGate = new();

    private readonly Dictionary<Account, long> _readSet = new();
    private readonly Dictionary<Account, int> _writeSet = new();

    private TxContext(int attempt) => Attempts = attempt;

    /// <summary>Номер текущей попытки (начинается с 1).</summary>
    public int Attempts { get; }

    /// <summary>
    /// Выполнить тело транзакции. При конфликте данные не меняются, и попытка
    /// повторяется целиком — это и есть «откат + retry» из лекции.
    /// </summary>
    /// <param name="body">Операции с памятью; всё общение со счетами идёт через контекст.</param>
    /// <param name="token">Отмена (кнопка «стоп»/«сброс» в приложении).</param>
    /// <param name="onConflict">Уведомление о конфликте: (номер попытки) — для визуализации.</param>
    /// <param name="onValidated">Результат валидации: (успех, номер попытки) — для визуализации.</param>
    /// <param name="maxAttempts">Предохранитель от бесконечного повтора.</param>
    public static TxResult Execute(
        Action<TxContext> body,
        CancellationToken token,
        Action<int>? onConflict = null,
        Action<bool, int>? onValidated = null,
        int maxAttempts = 1000)
    {
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (token.IsCancellationRequested)
                return new TxResult(TxOutcome.Cancelled, attempt);

            var context = new TxContext(attempt);
            _current = context;

            bool refused = false;
            try
            {
                body(context);                       // изменения пока только в журнале
            }
            catch (InsufficientFundsException)
            {
                refused = true;                      // бизнес-отказ, а не конфликт данных
            }
            finally
            {
                _current = null;                     // журнал не «протекает» наружу
            }

            if (refused)
                return new TxResult(TxOutcome.Refused, attempt);

            lock (CommitGate)                        // короткий критический участок
            {
                bool valid = context.Validate();
                onValidated?.Invoke(valid, attempt);

                if (valid)
                {
                    context.Apply();                 // фиксация: write-set → общая память
                    return new TxResult(TxOutcome.Committed, attempt);
                }
            }

            onConflict?.Invoke(attempt);
            // Попытка отброшена (журнал просто выброшен) — идём на новый круг.
        }

        return new TxResult(TxOutcome.Livelock, maxAttempts);
    }

    /// <summary>Чтение счёта внутри транзакции: значение попадает в read-set вместе с версией.</summary>
    public int Read(Account account)
    {
        // Своя же запись видна сразу (read-your-writes), как в настоящей STM.
        if (_writeSet.TryGetValue(account, out int written))
            return written;

        if (!account.TryReadConsistent(out int value, out long version))
            throw new InvalidOperationException("Не удалось получить согласованный снимок счёта");

        _readSet[account] = version;                 // эту версию проверим при фиксации
        return value;
    }

    /// <summary>Запись в локальный журнал: общая память не меняется до фиксации.</summary>
    public void Write(Account account, int value) => _writeSet[account] = value;

    /// <summary>Проверка конфликтов: не изменил ли кто-то то, что мы читали.</summary>
    private bool Validate()
    {
        foreach (var pair in _readSet)
        {
            if (pair.Key.Version != pair.Value)
                return false;                        // данные устарели
        }

        // Захваченный «пессимистичным» потоком счёт тоже конфликт: он изменит
        // значение в обход транзакции, и наша запись затрёт его работу.
        foreach (var pair in _writeSet)
        {
            if (pair.Key.IsLocked)
                return false;
        }

        foreach (var pair in _readSet)
        {
            if (pair.Key.IsLocked)
                return false;
        }

        return true;
    }

    /// <summary>Применение write-set к общей памяти.</summary>
    private void Apply()
    {
        foreach (var pair in _writeSet)
            pair.Key.SetBalance(pair.Value);

        _writeSet.Clear();
        _readSet.Clear();
    }
}
