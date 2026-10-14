namespace TmLab.Models;

/// <summary>
/// Набор счетов и главный инвариант работы: сумма денег на счетах неизменна
/// при любом порядке переводов.
/// </summary>
public sealed class Bank
{
    private static readonly string[] PeopleNames =
    {
        "Аня", "Борис", "Вика", "Гриша", "Дима", "Егор", "Жанна", "Зоя"
    };

    public Bank(int accountsCount, int total)
    {
        accountsCount = accountsCount < 1 ? 1 : accountsCount;
        Accounts = new Account[accountsCount];

        int each = total / accountsCount;
        for (int i = 0; i < accountsCount; i++)
            Accounts[i] = new Account(NameOf(i), each);

        StartTotal = each * accountsCount;
    }

    public Account[] Accounts { get; }

    /// <summary>Сумма, которая обязана сохраняться в демонстрации переводов.</summary>
    public int StartTotal { get; }

    public static string NameOf(int index) =>
        index >= 0 && index < PeopleNames.Length ? PeopleNames[index] : $"Счёт {index + 1}";

    /// <summary>Текущая сумма на всех счетах (для проверки инварианта).</summary>
    public int Total
    {
        get
        {
            int sum = 0;
            foreach (var account in Accounts)
                sum += account.Balance;

            return sum;
        }
    }
}
