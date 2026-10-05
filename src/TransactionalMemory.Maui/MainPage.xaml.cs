using System.Globalization;
using TransactionalMemory.Core;

namespace TransactionalMemory.Maui;

public partial class MainPage : ContentPage
{
    private const int MaxVisibleLogItems = 6;
    private readonly CultureInfo _russianCulture = CultureInfo.GetCultureInfo("ru-RU");
    private BankDemo _demo = new();
    private int _requestTotal;
    private int _attemptTotal;
    private int _conflictTotal;
    private bool _isBusy;

    public MainPage()
    {
        InitializeComponent();
        CountValueLabel.Text = FormatNumber((int)Math.Round(CountSlider.Value));
        RefreshBalances();
    }

    private void OnTransferCountChanged(object? sender, ValueChangedEventArgs e)
    {
        CountValueLabel.Text = FormatNumber((int)Math.Round(e.NewValue));
    }

    private async void OnTransferABClicked(object? sender, EventArgs e) => await RunSingleTransferAsync(fromA: true);

    private async void OnTransferBAClicked(object? sender, EventArgs e) => await RunSingleTransferAsync(fromA: false);

    private async Task RunSingleTransferAsync(bool fromA)
    {
        if (_isBusy || !TryReadAmount(out var amount))
        {
            return;
        }

        var from = fromA ? _demo.AccountA : _demo.AccountB;
        var to = fromA ? _demo.AccountB : _demo.AccountA;
        SetBusy(true);

        try
        {
            var receipt = await Task.Run(() => _demo.TryTransfer(from, to, amount));
            AddMetrics(requests: 1, attempts: receipt.Attempts, conflicts: receipt.Conflicts);
            RefreshBalances();

            if (receipt.Succeeded)
            {
                AddLog(
                    $"Перевод {from.Name} → {to.Name} зафиксирован",
                    $"{FormatMoney(amount)} · попыток: {FormatNumber(receipt.Attempts)} · конфликтов: {FormatNumber(receipt.Conflicts)}",
                    succeeded: true);
            }
            else
            {
                var snapshot = _demo.ReadSnapshot();
                var currentBalance = fromA ? snapshot.BalanceA : snapshot.BalanceB;
                AddLog(
                    "Недостаточно средств — без изменений",
                    $"На счёте {from.Name}: {FormatMoney(currentBalance)}, запрошено: {FormatMoney(amount)}. Ни один баланс не изменён.",
                    succeeded: false);
            }
        }
        catch (Exception exception)
        {
            AddLog("Транзакция не завершена", exception.Message, succeeded: false);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnParallelTestClicked(object? sender, EventArgs e)
    {
        if (_isBusy || !TryReadAmount(out var amount))
        {
            return;
        }

        var requestCount = (int)Math.Round(CountSlider.Value);
        SetBusy(true);

        try
        {
            var summary = await Task.Run(() => _demo.RunParallelTest(amount, requestCount));
            AddMetrics(summary.Requests, summary.Attempts, summary.Conflicts);
            var snapshot = _demo.ReadSnapshot();
            RefreshBalances(snapshot);

            AddLog(
                "Параллельный тест завершён",
                $"Запросов: {FormatNumber(summary.Requests)} · переводов: {FormatNumber(summary.CompletedTransfers)} · отказов: {FormatNumber(summary.RejectedTransfers)} · конфликтов: {FormatNumber(summary.Conflicts)}. Общая сумма: {FormatMoney(snapshot.Total)}.",
                succeeded: snapshot.Total == BankDemo.InitialBalance * 2m);
        }
        catch (Exception exception)
        {
            AddLog("Параллельный тест остановлен", exception.Message, succeeded: false);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnResetClicked(object? sender, EventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        _demo = new BankDemo();
        _requestTotal = 0;
        _attemptTotal = 0;
        _conflictTotal = 0;
        UpdateMetricsLabels();
        LogStack.Children.Clear();
        LogEmptyLabel.IsVisible = true;
        RefreshBalances();
        AddLog("Эксперимент сброшен", "Оба счёта восстановлены до начального баланса.", succeeded: true);
    }

    private bool TryReadAmount(out decimal amount)
    {
        var raw = AmountEntry.Text?.Trim().Replace(" ", string.Empty).Replace("\u00A0", string.Empty);
        const NumberStyles styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;

        if (decimal.TryParse(raw, styles, _russianCulture, out amount) ||
            decimal.TryParse(raw, styles, CultureInfo.InvariantCulture, out amount))
        {
            if (amount > 0)
            {
                return true;
            }
        }

        amount = 0;
        AddLog("Проверьте сумму", "Введите положительное число, например 10 или 10,50.", succeeded: false);
        return false;
    }

    private void AddMetrics(int requests, int attempts, int conflicts)
    {
        _requestTotal = checked(_requestTotal + requests);
        _attemptTotal = checked(_attemptTotal + attempts);
        _conflictTotal = checked(_conflictTotal + conflicts);
        UpdateMetricsLabels();
    }

    private void UpdateMetricsLabels()
    {
        RequestsValueLabel.Text = FormatNumber(_requestTotal);
        AttemptsValueLabel.Text = FormatNumber(_attemptTotal);
        ConflictsValueLabel.Text = FormatNumber(_conflictTotal);
    }

    private void RefreshBalances() => RefreshBalances(_demo.ReadSnapshot());

    private void RefreshBalances(BankSnapshot snapshot)
    {
        BalanceALabel.Text = FormatMoney(snapshot.BalanceA);
        BalanceBLabel.Text = FormatMoney(snapshot.BalanceB);
        VersionALabel.Text = $"версия {FormatNumber(snapshot.VersionA)}";
        VersionBLabel.Text = $"версия {FormatNumber(snapshot.VersionB)}";
        TotalBalanceLabel.Text = FormatMoney(snapshot.Total);
    }

    private void AddLog(string title, string details, bool succeeded)
    {
        LogEmptyLabel.IsVisible = false;

        var accent = Color.FromArgb(succeeded ? "#117C68" : "#B54E49");
        var marker = new Border
        {
            WidthRequest = 8,
            HeightRequest = 8,
            Margin = new Thickness(0, 5, 0, 0),
            BackgroundColor = accent,
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = new CornerRadius(4) },
            VerticalOptions = LayoutOptions.Start
        };

        var labels = new VerticalStackLayout { Spacing = 3 };
        labels.Children.Add(new Label
        {
            Text = title,
            FontSize = 13,
            FontAttributes = FontAttributes.Bold,
            TextColor = accent
        });
        labels.Children.Add(new Label
        {
            Text = details,
            FontSize = 11,
            LineBreakMode = LineBreakMode.WordWrap,
            TextColor = Color.FromArgb("#6D817C")
        });

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition { Width = new GridLength(14) },
                new ColumnDefinition { Width = GridLength.Star }
            },
            ColumnSpacing = 8
        };
        row.Children.Add(marker);
        row.Children.Add(labels);
        Grid.SetColumn(labels, 1);

        var item = new Border
        {
            Padding = new Thickness(13, 11),
            BackgroundColor = Colors.White,
            Stroke = Color.FromArgb("#DEE8E3"),
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = new CornerRadius(14) },
            Content = row
        };

        LogStack.Children.Insert(0, item);
        while (LogStack.Children.Count > MaxVisibleLogItems)
        {
            LogStack.Children.RemoveAt(LogStack.Children.Count - 1);
        }
    }

    private void SetBusy(bool isBusy)
    {
        _isBusy = isBusy;
        TransferABButton.IsEnabled = !isBusy;
        TransferBAButton.IsEnabled = !isBusy;
        ParallelTestButton.IsEnabled = !isBusy;
        ResetButton.IsEnabled = !isBusy;
        AmountEntry.IsEnabled = !isBusy;
        CountSlider.IsEnabled = !isBusy;
        BusyRow.IsVisible = isBusy;
        BusyIndicator.IsRunning = isBusy;
    }

    private string FormatMoney(decimal value) => $"{value.ToString("N2", _russianCulture)} ₽";

    private string FormatNumber(long value) => value.ToString("N0", _russianCulture);
}
