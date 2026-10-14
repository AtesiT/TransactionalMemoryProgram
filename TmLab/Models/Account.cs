using System.Threading;

namespace TmLab.Models;

/// <summary>
/// Банковский счёт: значение, версия данных и признак блокировки.
///
/// Один и тот же класс используется всеми тремя способами синхронизации
/// из лабораторной работы:
///   * транзакции (STM)   — чтение с версией, запись на этапе фиксации;
///   * блокировки (lock)  — захват счёта через TryLock;
///   * CAS-цикл           — TryCompareExchange (Пример 1 из задания).
/// </summary>
public sealed class Account
{
    private int _balance;              // значение счёта
    private long _version = 1;         // «штамп» версии данных
    private int _locked;               // 0 — свободен, 1 — захвачен

    public Account(string name, int balance)
    {
        Name = name;
        _balance = balance;
    }

    public string Name { get; }

    public int Balance => Volatile.Read(ref _balance);

    public long Version => Interlocked.Read(ref _version);

    public bool IsLocked => Volatile.Read(ref _locked) != 0;

    // --- Пример 1: сравнение с обменом (аналог Interlocked.CompareExchange) --

    /// <summary>
    /// Записать новое значение, только если текущее всё ещё равно ожидаемому.
    /// false — значит, другой поток успел изменить счёт: транзакция повторяется.
    /// </summary>
    public bool TryCompareExchange(int expected, int desired)
    {
        if (Interlocked.CompareExchange(ref _balance, desired, expected) != expected)
            return false;

        Publish();
        return true;
    }

    // --- Пример 2: транзакционное чтение и фиксация -------------------------

    /// <summary>
    /// Согласованное чтение пары «значение + версия».
    ///
    /// Версия читается до и после значения: если между чтениями другой поток
    /// успел зафиксировать транзакцию, получилась бы пара «старое значение,
    /// новая версия», и валидация пропустила бы конфликт. Приём называется
    /// seqlock. Заблокированный счёт тоже не читаем — его значение изменится
    /// в обход транзакций.
    /// </summary>
    public bool TryReadConsistent(out int value, out long version)
    {
        for (int attempt = 0; attempt < 100_000; attempt++)
        {
            long before = Version;
            int snapshot = Balance;
            long after = Version;

            if (before == after && !IsLocked)
            {
                value = snapshot;
                version = before;
                return true;
            }
        }

        value = Balance;
        version = Version;
        return false;
    }

    /// <summary>Запись значения при фиксации транзакции (вызывается под общим замком фиксации).</summary>
    public void SetBalance(int value)
    {
        Volatile.Write(ref _balance, value);
        Publish();
    }

    /// <summary>
    /// Запись в обход синхронизации — только для демонстрации «сломанного» режима.
    /// Версия при этом НЕ публикуется: транзакции такие изменения не заметят,
    /// поэтому оптимистичный подход ломается, если писать в память мимо него.
    /// </summary>
    public void SetBalanceUnsafe(int value) => _balance = value;

    /// <summary>Публикация новой версии данных: транзакции увидят, что их чтения устарели.</summary>
    public void Publish() => Interlocked.Increment(ref _version);

    // --- блокировки (пессимистичный подход) --------------------------------

    public bool TryLock() => Interlocked.CompareExchange(ref _locked, 1, 0) == 0;

    public void Unlock() => Volatile.Write(ref _locked, 0);
}
