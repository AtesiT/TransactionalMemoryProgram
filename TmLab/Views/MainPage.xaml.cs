using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;
using TmLab.Drawing;
using TmLab.Models;
using TmLab.Simulation;

namespace TmLab;

/// <summary>
/// Страница демонстрации: запускает потоки, забирает их события из очереди и
/// рисует схему, таймлайн, состояния потоков и журнал.
///
/// Важное правило: с потоками симуляции интерфейс общается ТОЛЬКО через
/// очередь событий (SimulationRunner.Events). Всё рисование и обновление
/// надписей идёт в UI-потоке по таймеру, поэтому ничего не «дёргается»
/// и не требует блокировок.
/// </summary>
public partial class MainPage : ContentPage
{
    private const int MaxWorkers = 16;
    private const int JournalLines = 16;

    private readonly NetworkDrawable _network = new();
    private readonly TimelineDrawable _timeline = new();

    // карточки потоков (создаются один раз, дальше только меняем текст)
    private readonly List<Border> _workerCards = new();
    private readonly List<Label> _workerStateLabels = new();
    private readonly List<Label> _workerStatsLabels = new();

    private readonly int[] _workerOps = new int[MaxWorkers];
    private readonly int[] _workerRetries = new int[MaxWorkers];
    private readonly int[] _workerWaits = new int[MaxWorkers];
    private readonly string[] _workerStates = new string[MaxWorkers];
    private readonly long[] _workerLastEvent = new long[MaxWorkers];

    private readonly List<LogLine> _log = new();

    private SimulationRunner? _runner;
    private IDispatcherTimer? _timer;

    private int _demo = 1;
    private Technique _technique = Technique.Cas;
    private int _workers = 8;
    private string _activePanel = "workers";
    private int _filterWorker = -1;
    private bool _paused;

    private long _rateBaseMs;
    private int _rateBaseOps;
    private double _rate;
    private long _invariantBadSince;

    // раскладка: узкое окно — одна колонка со прокруткой, широкое — две колонки
    private bool _wideLayout;
    private double _layoutWidth = -1;
    private double _layoutHeight = -1;

    private sealed class LogLine
    {
        public string Text { get; init; } = string.Empty;

        public int WorkerId { get; init; }

        /// <summary>Важные события видны всегда, остальные — только в фильтре по потоку.</summary>
        public bool Notable { get; init; }
    }

    public MainPage()
    {
        InitializeComponent();

        NetworkView.Drawable = _network;
        TimelineView.Drawable = _timeline;

        BuildWorkerCards();
        ApplyDemoUi();
        UpdateSpeedLabels();
        ResetVisuals();
    }

    // -------------------------------------------------------------------------
    // Жизненный цикл
    // -------------------------------------------------------------------------

    protected override void OnAppearing()
    {
        base.OnAppearing();

        _timer ??= CreateTimer();
        _timer.Start();
    }

    /// <summary>
    /// Окно изменило размер — подстраиваем раскладку. Вызывается и при первом
    /// показе страницы, поэтому начальная раскладка тоже задаётся здесь.
    /// </summary>
    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);

        if (width <= 0 || height <= 0)
            return;

        // мелкие изменения игнорируем, чтобы не дёргать раскладку на каждый пиксель
        if (Math.Abs(width - _layoutWidth) < 16 && Math.Abs(height - _layoutHeight) < 16)
            return;

        _layoutWidth = width;
        _layoutHeight = height;
        ApplyAdaptiveLayout(width, height);
    }

    /// <summary>
    /// Адаптивная раскладка.
    ///
    /// Широкое окно (планшет, компьютер): две колонки — слева настройки,
    /// показатели, инвариант и кнопки, справа схема и панели. Всё помещается
    /// в экран, прокрутка не нужна.
    ///
    /// Узкое окно (телефон): одна колонка, схема сверху. Прокрутка есть, но
    /// пользоваться ею почти не придётся, потому что высота схемы считается
    /// от высоты окна.
    /// </summary>
    private void ApplyAdaptiveLayout(double width, double height)
    {
        bool wide = width >= 860;

        // схема — главный элемент: примерно 40–50 % высоты окна
        SchemeSection.HeightRequest = Math.Clamp(height * (wide ? 0.46 : 0.36), 200, 520);
        PanelHost.HeightRequest = Math.Clamp(height * (wide ? 0.30 : 0.22), 130, 280);

        if (wide == _wideLayout && PageRoot.ColumnDefinitions.Count > 0)
            return;                                   // колонки уже расставлены

        _wideLayout = wide;

        PageRoot.ColumnDefinitions.Clear();
        if (wide)
        {
            PageRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            PageRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
        }
        else
        {
            PageRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
        }

        // элементы одни и те же — переносим их между колонками, состояние сохраняется
        Grid.SetColumn(LeftStack, 0);
        Grid.SetColumn(RightStack, wide ? 1 : 0);      // при одной колонке — тоже 0, иначе Grid ругается

        LeftStack.Children.Clear();
        RightStack.Children.Clear();
        RightStack.IsVisible = wide;
        LeftStack.WidthRequest = wide ? 380 : -1;

        View[] left = wide
            ? new View[] { DemoSwitchPanel, ParamsBlock, MetricsPanel, InvariantPanel, ControlsBlock }
            : new View[] { DemoSwitchPanel, ParamsBlock, SchemeSection, MetricsPanel, InvariantPanel,
                           PanelSwitchPanel, PanelHost, ControlsBlock };

        View[] right = wide
            ? new View[] { SchemeSection, PanelSwitchPanel, PanelHost }
            : Array.Empty<View>();

        foreach (var section in left)
            LeftStack.Children.Add(section);

        foreach (var section in right)
            RightStack.Children.Add(section);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        _timer?.Stop();
        _runner?.Stop();
    }

    private IDispatcherTimer CreateTimer()
    {
        var timer = Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(40);      // ~25 кадров в секунду
        timer.Tick += OnTimerTick;
        return timer;
    }

    // -------------------------------------------------------------------------
    // Основной цикл интерфейса
    // -------------------------------------------------------------------------

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (_runner is null)
            return;

        DrainEvents();

        OpsLabel.Text = _runner.Operations.ToString("N0");
        CommitsLabel.Text = _runner.Commits.ToString("N0");
        RetriesLabel.Text = _runner.Retries.ToString("N0");

        if (_demo == 2)
            RefusalsLabel.Text = _runner.Refusals.ToString("N0");
        else
            RefusalsLabel.Text = _runner.Waits.ToString("N0");    // в зачислении ожиданий больше

        UpdateRate();
        UpdateEfficiency();
        UpdateInvariant();
        UpdateWorkerCards();

        if (_activePanel == "journal")
            RenderJournal();

        // Монетки летят тем медленнее, чем медленнее выбранный темп:
        // на «показе» успеваешь разглядеть каждый перевод.
        _network.FlightScale = Math.Clamp(GapSlider.Value / 300.0, 0.5, 2.4);

        _network.Workers = _workers;
        _network.Update(TakeSnapshot());

        NetworkView.Invalidate();

        if (_activePanel == "timeline")
        {
            _timeline.Workers = _workers;
            TimelineView.Invalidate();
        }
    }

    /// <summary>Снимок состояния банка: числа, которые нужно нарисовать.</summary>
    private ModelSnapshot TakeSnapshot()
    {
        if (_runner is null)
            return new ModelSnapshot();

        var accounts = _runner.Bank.Accounts;
        var names = new string[accounts.Length];
        var balances = new int[accounts.Length];
        var versions = new long[accounts.Length];
        var locked = new bool[accounts.Length];

        for (int i = 0; i < accounts.Length; i++)
        {
            names[i] = accounts[i].Name;
            balances[i] = accounts[i].Balance;
            versions[i] = accounts[i].Version;
            locked[i] = accounts[i].IsLocked;
        }

        return new ModelSnapshot
        {
            Names = names,
            Balances = balances,
            Versions = versions,
            Locked = locked
        };
    }

    // -------------------------------------------------------------------------
    // Разбор событий потоков
    // -------------------------------------------------------------------------

    private void DrainEvents()
    {
        if (_runner is null)
            return;

        int processed = 0;

        // Ограничиваем порцию за один кадр, чтобы интерфейс не захлебнулся.
        while (processed < 400 && _runner.TryDequeueEvent(out var workerEvent))
        {
            HandleEvent(workerEvent);
            processed++;
        }
    }

    private void HandleEvent(WorkerEvent workerEvent)
    {
        long now = Environment.TickCount64;
        int worker = workerEvent.WorkerId;

        if (worker >= 0 && worker < MaxWorkers)
            _workerLastEvent[worker] = now;

        switch (workerEvent.Kind)
        {
            case WorkerEventKind.OpBegin:
                _timeline.AttemptBegin(worker, now);
                SetWorkerState(worker, "операция");
                break;

            case WorkerEventKind.Read:
                _timeline.AddPhase(worker, "read", now);
                SetWorkerState(worker, "чтение");
                Log(worker, $"#{worker}  чтение счёта {Letter(workerEvent.From)}", false);
                break;

            case WorkerEventKind.Write:
                _timeline.AddPhase(worker, "compute", now);
                SetWorkerState(worker, "вычисления");
                Log(worker, $"#{worker}  запись в журнал {Letter(workerEvent.From)} → {Letter(workerEvent.To)}", false);
                break;

            case WorkerEventKind.Validate:
                _timeline.AddPhase(worker, "validate", now);
                SetWorkerState(worker, "валидация");
                Log(worker, $"#{worker}  валидация версий", false);
                break;

            case WorkerEventKind.Lock:
                _timeline.AddPhase(worker, "wait", now);
                SetWorkerState(worker, "счёт захвачен");
                break;

            case WorkerEventKind.Wait:
                _timeline.AddPhase(worker, "wait", now);
                SetWorkerState(worker, "ожидание счёта");
                if (worker >= 0 && worker < MaxWorkers)
                    _workerWaits[worker]++;

                Log(worker, $"#{worker}  ждёт освобождения счёта {Letter(workerEvent.From)}", true);
                break;

            case WorkerEventKind.Unlock:
                break;

            case WorkerEventKind.Commit:
                _timeline.AddPhase(worker, "commit", now);
                _timeline.AttemptEnd(worker, now, false);
                SetWorkerState(worker, "коммит");
                if (worker >= 0 && worker < MaxWorkers)
                    _workerOps[worker]++;

                if (_demo == 1)
                {
                    _network.Send(-1, 0, workerEvent.Amount, CoinStatus.Ok, worker);
                    Log(worker, $"#{worker}  CompareExchange прошёл: +{workerEvent.Amount} ₽ → счёт", true);
                }
                else
                {
                    _network.Send(workerEvent.From, workerEvent.To, workerEvent.Amount, CoinStatus.Ok, worker);
                    Log(worker, $"#{worker}  перевод {workerEvent.Amount} ₽: " +
                                $"{Letter(workerEvent.From)} → {Letter(workerEvent.To)} зафиксирован", true);
                }

                break;

            case WorkerEventKind.Conflict:
                _timeline.AddPhase(worker, "conflict", now);
                _timeline.AttemptEnd(worker, now, true);
                SetWorkerState(worker, "конфликт → повтор");
                if (worker >= 0 && worker < MaxWorkers)
                    _workerRetries[worker]++;

                if (_demo == 1)
                {
                    _network.Send(-1, 0, workerEvent.Amount, CoinStatus.Conflict, worker);
                    Log(worker, $"#{worker}  конфликт CAS: баланс изменили, повтор", true);
                }
                else
                {
                    _network.Send(workerEvent.From, workerEvent.To, workerEvent.Amount, CoinStatus.Conflict, worker);
                    Log(worker, $"#{worker}  конфликт версий: перевод {workerEvent.Amount} ₽ не прошёл, откат", true);
                }

                break;

            case WorkerEventKind.Refused:
                _timeline.AttemptEnd(worker, now, true);
                SetWorkerState(worker, "нет средств");
                if (worker >= 0 && worker < MaxWorkers)
                    _workerOps[worker]++;

                _network.Send(workerEvent.From, workerEvent.To, workerEvent.Amount, CoinStatus.Refused, worker);
                Log(worker, $"#{worker}  отказ: недостаточно средств для {workerEvent.Amount} ₽", true);
                break;

            case WorkerEventKind.Idle:
                _timeline.AddPhase(worker, "idle", now);
                SetWorkerState(worker, "пауза между операциями");
                break;
        }
    }

    private static string Letter(int index) =>
        index >= 0 && index < 26 ? ((char)('A' + index)).ToString() : index.ToString();

    private void SetWorkerState(int worker, string text)
    {
        if (worker < 0 || worker >= MaxWorkers)
            return;

        _workerStates[worker] = text;
    }

    private void Log(int workerId, string text, bool notable)
    {
        _log.Add(new LogLine { Text = text, WorkerId = workerId, Notable = notable });

        if (_log.Count > 400)
            _log.RemoveRange(0, 200);
    }

    private void RenderJournal()
    {
        var builder = new StringBuilder();

        if (_filterWorker >= 0)
            builder.AppendLine($"фильтр: поток #{_filterWorker} (нажмите карточку ещё раз, чтобы снять)");

        int shown = 0;
        for (int i = _log.Count - 1; i >= 0 && shown < JournalLines; i--)
        {
            var line = _log[i];

            if (_filterWorker >= 0)
            {
                if (line.WorkerId != _filterWorker)
                    continue;
            }
            else if (!line.Notable)
            {
                continue;                        // без фильтра показываем только важное
            }

            builder.Insert(0, line.Text + Environment.NewLine);
            shown++;
        }

        JournalLabel.Text = shown == 0 ? "пока событий нет" : builder.ToString().TrimEnd();
    }

    // -------------------------------------------------------------------------
    // Показатели
    // -------------------------------------------------------------------------

    private void UpdateRate()
    {
        if (_runner is null)
            return;

        long now = Environment.TickCount64;
        if (_rateBaseMs == 0)
        {
            _rateBaseMs = now;
            _rateBaseOps = _runner.Operations;
            return;
        }

        long elapsed = now - _rateBaseMs;
        if (elapsed < 700)
            return;

        _rate = (_runner.Operations - _rateBaseOps) / (elapsed / 1000.0);
        _rateBaseMs = now;
        _rateBaseOps = _runner.Operations;
        RateLabel.Text = _rate.ToString("0");
    }

    private void UpdateEfficiency()
    {
        if (_runner is null)
            return;

        int attempts = _runner.Commits + _runner.Retries;
        EfficiencyLabel.Text = attempts == 0 ? "—" : (_runner.Commits * 100.0 / attempts).ToString("0.#") + "%";
    }

    private void UpdateInvariant()
    {
        if (_runner is null)
        {
            InvariantLabel.Text = "ожидание запуска";
            InvariantLabel.TextColor = Color.FromArgb("#8fa3c4");
            return;
        }

        int total = _runner.Bank.Total;
        int expected = _demo == 1
            ? _runner.Bank.StartTotal + _runner.Options.Amount * _runner.Operations
            : _runner.Bank.StartTotal;

        if (total == expected)
        {
            _invariantBadSince = 0;
            InvariantLabel.TextColor = Color.FromArgb("#35d07f");
            InvariantLabel.Text = _demo == 1
                ? $"Все пополнения учтены: {total:N0} ₽ ✔"
                : $"Сумма на счетах {total:N0} ₽ — не изменилась ✔";
            return;
        }

        // Снимок состояния и счётчики читаются не строго одновременно, поэтому
        // тревогу показываем, только если расхождение держится дольше 400 мс.
        long now = Environment.TickCount64;
        if (_invariantBadSince == 0)
        {
            _invariantBadSince = now;
            InvariantLabel.TextColor = Color.FromArgb("#8fa3c4");
            InvariantLabel.Text = "сверка значений…";
            return;
        }

        if (now - _invariantBadSince < 400)
            return;

        int difference = expected - total;
        InvariantLabel.TextColor = Color.FromArgb("#ff5d6c");
        InvariantLabel.Text = _demo == 1
            ? $"Потеряно {difference:N0} ₽: на счету {total:N0} вместо {expected:N0}"
            : $"Сумма изменилась на {total - expected:+#;-#;0} ₽: сейчас {total:N0} вместо {expected:N0}";
    }

    private void UpdateWorkerCards()
    {
        long now = Environment.TickCount64;

        for (int i = 0; i < MaxWorkers; i++)
        {
            bool visible = i < _workers;
            _workerCards[i].IsVisible = visible;

            if (!visible)
                continue;

            // поток давно не подавал признаков жизни — считаем, что он на паузе
            string state = _workerStates[i];
            if (now - _workerLastEvent[i] > 900 || _workerLastEvent[i] == 0)
                state = "пауза";

            _workerStateLabels[i].Text = state;
            _workerStateLabels[i].TextColor = ColorForState(state);
            _workerStatsLabels[i].Text =
                $"оп {_workerOps[i]} · retry {_workerRetries[i]} · ожид {_workerWaits[i]}";

            bool filtered = _filterWorker == i;
            _workerCards[i].Stroke = filtered
                ? Color.FromArgb("#4ea1ff")
                : Color.FromArgb("#22304f");
            _workerCards[i].StrokeThickness = filtered ? 2 : 1;
        }
    }

    private static Color ColorForState(string state)
    {
        if (state.StartsWith("чтение", StringComparison.Ordinal))
            return Color.FromArgb("#4ea1ff");
        if (state.StartsWith("вычисления", StringComparison.Ordinal))
            return Color.FromArgb("#58c0ff");
        if (state.StartsWith("валидация", StringComparison.Ordinal))
            return Color.FromArgb("#a78bfa");
        if (state.StartsWith("коммит", StringComparison.Ordinal))
            return Color.FromArgb("#35d07f");
        if (state.StartsWith("конфликт", StringComparison.Ordinal) ||
            state.StartsWith("нет средств", StringComparison.Ordinal))
            return Color.FromArgb("#ff5d6c");
        if (state.StartsWith("ожидание", StringComparison.Ordinal) ||
            state.StartsWith("счёт захвачен", StringComparison.Ordinal))
            return Color.FromArgb("#ffb020");

        return Color.FromArgb("#8fa3c4");
    }

    // -------------------------------------------------------------------------
    // Карточки потоков
    // -------------------------------------------------------------------------

    private void BuildWorkerCards()
    {
        WorkersGrid.Children.Clear();
        WorkersGrid.RowDefinitions.Clear();

        for (int row = 0; row < MaxWorkers / 2; row++)
            WorkersGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int i = 0; i < MaxWorkers; i++)
        {
            int workerId = i;

            var title = new Label
            {
                Text = $"поток #{i}",
                TextColor = Color.FromArgb("#8fa3c4"),
                FontSize = 10
            };

            var state = new Label
            {
                Text = "пауза",
                TextColor = Color.FromArgb("#8fa3c4"),
                FontSize = 12
            };

            var stats = new Label
            {
                Text = "оп 0 · retry 0 · ожид 0",
                TextColor = Color.FromArgb("#6d80a3"),
                FontSize = 10
            };

            var stack = new VerticalStackLayout { Spacing = 1 };
            stack.Add(title);
            stack.Add(state);
            stack.Add(stats);

            var card = new Border
            {
                Stroke = Color.FromArgb("#22304f"),
                StrokeThickness = 1,
                BackgroundColor = Color.FromArgb("#0c1224"),
                StrokeShape = new RoundRectangle { CornerRadius = 8 },
                Padding = new Thickness(8, 5),
                Content = stack
            };

            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => ToggleFilter(workerId);
            card.GestureRecognizers.Add(tap);

            _workerCards.Add(card);
            _workerStateLabels.Add(state);
            _workerStatsLabels.Add(stats);

            WorkersGrid.Add(card, i % 2, i / 2);
        }
    }

    private void ToggleFilter(int workerId)
    {
        _filterWorker = _filterWorker == workerId ? -1 : workerId;
        RenderJournal();
    }

    // -------------------------------------------------------------------------
    // Кнопки: пример, сценарий, темп, панели, запуск
    // -------------------------------------------------------------------------

    private void OnDemo1Clicked(object? sender, EventArgs e) => SwitchDemo(1);

    private void OnDemo2Clicked(object? sender, EventArgs e) => SwitchDemo(2);

    private void SwitchDemo(int demo)
    {
        if (_demo == demo)
            return;

        _demo = demo;
        _technique = demo == 1 ? Technique.Cas : Technique.Stm;

        ApplyDemoUi();
        ResetVisuals();

        if (_runner is { IsRunning: true })
            StartNewRun();
    }

    /// <summary>Тексты кнопок и подписи, зависящие от выбранного примера.</summary>
    private void ApplyDemoUi()
    {
        bool first = _demo == 1;

        Demo1Button.BackgroundColor = first ? Color.FromArgb("#2b6cff") : Color.FromArgb("#16213c");
        Demo1Button.TextColor = first ? Colors.White : Color.FromArgb("#8fa3c4");
        Demo2Button.BackgroundColor = first ? Color.FromArgb("#16213c") : Color.FromArgb("#2b6cff");
        Demo2Button.TextColor = first ? Color.FromArgb("#8fa3c4") : Colors.White;

        TechButtonA.Text = first ? "CAS-цикл" : "STM";
        TechButtonB.Text = "Блокировки";
        TechButtonC.Text = "Без синхронизации";

        NetTitleLabel.Text = first
            ? "Зачисление на счёт (потоки → одна ячейка памяти)"
            : "Переводы между счетами (линия = попытка транзакции)";

        NetHintLabel.Text = first
            ? "Кружки по краям — потоки. Монетка долетает до счёта только тогда, когда CompareExchange прошёл; при конфликте монетка гаснет и операция повторяется."
            : "Зелёная монетка долетела — транзакция зафиксирована. Красная погасла на полпути — конфликт версий: транзакция откатилась и будет переиграна.";

        RefusalCaption.Text = first ? "Ожиданий" : "Отказов";

        _network.IsDepositDemo = first;
        _network.Caption = string.Empty;
        _network.CaptionColor = Color.FromArgb("#6d80a3");

        UpdateTechniqueButtons();
    }

    private void OnTechniqueAClicked(object? sender, EventArgs e)
    {
        _technique = _demo == 1 ? Technique.Cas : Technique.Stm;
        UpdateTechniqueButtons();
        if (_runner is { IsRunning: true })
            StartNewRun();
    }

    private void OnTechniqueBClicked(object? sender, EventArgs e)
    {
        _technique = Technique.Lock;
        UpdateTechniqueButtons();
        if (_runner is { IsRunning: true })
            StartNewRun();
    }

    private void OnTechniqueCClicked(object? sender, EventArgs e)
    {
        _technique = Technique.Unsafe;
        UpdateTechniqueButtons();
        if (_runner is { IsRunning: true })
            StartNewRun();
    }

    private void UpdateTechniqueButtons()
    {
        var buttons = new[] { TechButtonA, TechButtonB, TechButtonC };
        var techniques = new[] { _demo == 1 ? Technique.Cas : Technique.Stm, Technique.Lock, Technique.Unsafe };

        for (int i = 0; i < buttons.Length; i++)
        {
            bool active = techniques[i] == _technique;
            buttons[i].BackgroundColor = active ? Color.FromArgb("#2b6cff") : Color.FromArgb("#16213c");
            buttons[i].TextColor = active ? Colors.White : Color.FromArgb("#8fa3c4");
        }

        TechniqueHintLabel.Text = _technique switch
        {
            Technique.Stm => "Перевод целиком в транзакции: чтения в журнал, валидация версий, фиксация. При конфликте — откат и повтор.",
            Technique.Lock => "Счета захватываются по очереди (в порядке возрастания номера — иначе была бы взаимная блокировка). Остальные потоки ждут.",
            Technique.Cas when _demo == 1 => "Оптимистичная блокировка: читаем баланс, считаем новое значение и пробуем заменить его через CompareExchange. Не вышло — повторяем.",
            Technique.Cas => "Оптимистичная блокировка на одном счёте.",
            _ => "Синхронизации нет вообще: часть операций теряется (или деньги появляются из ниоткуда). Это и показывает, зачем нужна синхронизация."
        };

        _network.Caption = _technique == Technique.Unsafe
            ? "БЕЗ СИНХРОНИЗАЦИИ: транзакций нет, часть изменений теряется"
            : string.Empty;
        _network.CaptionColor = Color.FromArgb("#ff5d6c");
    }

    private void OnTempoShowClicked(object? sender, EventArgs e) => ApplyTempo("show");

    private void OnTempoNormalClicked(object? sender, EventArgs e) => ApplyTempo("normal");

    private void OnTempoLoadClicked(object? sender, EventArgs e) => ApplyTempo("load");

    /// <summary>
    /// Готовые темпы показа. Значения ползунков выставляем сами — их обработчики
    /// сразу же передадут новые параметры в работающие потоки.
    /// </summary>
    private void ApplyTempo(string name)
    {
        var preset = name switch
        {
            "show" => SimulationOptions.Show,
            "load" => SimulationOptions.Load,
            _ => SimulationOptions.Normal
        };

        GapSlider.Value = preset.GapMs;
        WorkSlider.Value = preset.WorkMicroseconds;

        var buttons = new[] { TempoShowButton, TempoNormalButton, TempoLoadButton };
        foreach (var button in buttons)
        {
            button.BackgroundColor = Color.FromArgb("#16213c");
            button.TextColor = Color.FromArgb("#8fa3c4");
        }

        var active = name switch
        {
            "show" => TempoShowButton,
            "load" => TempoLoadButton,
            _ => TempoNormalButton
        };

        active.BackgroundColor = Color.FromArgb("#2b6cff");
        active.TextColor = Colors.White;

        _timeline.SetWindow(preset.GapMs >= 700 ? 18000 : preset.GapMs >= 250 ? 12000 : 7000);
    }

    private void OnParamsToggleClicked(object? sender, EventArgs e)
    {
        ParamsPanel.IsVisible = !ParamsPanel.IsVisible;
        ParamsToggleButton.Text = ParamsPanel.IsVisible ? "▾ Параметры запуска" : "▸ Параметры запуска";
    }

    private void OnWorkersChanged(object? sender, ValueChangedEventArgs e)
    {
        _workers = Math.Clamp((int)Math.Round(e.NewValue), 2, MaxWorkers);

        if (WorkersLabel is not null)
        {
            WorkersLabel.Text = $"Потоков: {_workers}";
            UpdateSpeedLabels();                       // оценка «операций/с» зависит от числа потоков
        }

        if (_runner is { IsRunning: true })
            StartNewRun();
    }

    private void OnWorkChanged(object? sender, ValueChangedEventArgs e)
    {
        int value = (int)Math.Round(e.NewValue);

        if (WorkLabel is not null)
            UpdateSpeedLabels();

        if (_runner is not null)
            _runner.Options.WorkMicroseconds = value;   // потоки читают значение на каждой операции
    }

    /// <summary>Подписи к ползункам скорости (вызывается и при старте страницы).</summary>
    private void UpdateSpeedLabels()
    {
        int gap = (int)Math.Round(GapSlider.Value);
        int work = (int)Math.Round(WorkSlider.Value);

        // прикидка темпа: столько операций успевают сделать все потоки вместе
        double perOperation = gap / 1000.0 + work / 1_000_000.0;
        string rate = perOperation > 0
            ? $"≈{_workers / perOperation:0} операций/с"
            : "без пауз, в упор";

        string word = gap >= 800 ? "медленно" : gap >= 250 ? "обычная" : "быстро";

        GapLabel.Text = $"Скорость: {word} — пауза {gap} мс, {rate} при {_workers} потоках";
        WorkLabel.Text = $"Работа в транзакции: {work} мкс (дольше операция — чаще конфликты)";
    }

    private void OnGapChanged(object? sender, ValueChangedEventArgs e)
    {
        int value = (int)Math.Round(e.NewValue);

        if (GapLabel is not null)
            UpdateSpeedLabels();

        if (_runner is not null)
            _runner.Options.GapMs = value;

        // окно таймлайна подстраиваем под темп: на медленном видно больше истории
        _timeline.SetWindow(value >= 700 ? 18000 : value >= 250 ? 12000 : 7000);
    }

    private void OnPanelWorkersClicked(object? sender, EventArgs e) => ShowPanel("workers");

    private void OnPanelTimelineClicked(object? sender, EventArgs e) => ShowPanel("timeline");

    private void OnPanelJournalClicked(object? sender, EventArgs e) => ShowPanel("journal");

    private void ShowPanel(string panel)
    {
        _activePanel = panel;

        WorkersPanel.IsVisible = panel == "workers";
        TimelinePanel.IsVisible = panel == "timeline";
        JournalPanel.IsVisible = panel == "journal";

        var buttons = new[]
        {
            (PanelWorkersButton, "workers"),
            (PanelTimelineButton, "timeline"),
            (PanelJournalButton, "journal")
        };

        foreach (var (button, name) in buttons)
        {
            bool active = name == panel;
            button.BackgroundColor = active ? Color.FromArgb("#2b6cff") : Color.FromArgb("#16213c");
            button.TextColor = active ? Colors.White : Color.FromArgb("#8fa3c4");
        }

        if (panel == "timeline")
            TimelineView.Invalidate();

        if (panel == "journal")
            RenderJournal();
    }

    // -------------------------------------------------------------------------
    // Запуск, пауза, сброс
    // -------------------------------------------------------------------------

    private void OnStartClicked(object? sender, EventArgs e)
    {
        if (_paused && _runner is not null)
        {
            _runner.Paused = false;                 // продолжаем с того же места
            _paused = false;
            PauseButton.Text = "⏸ Пауза";
            StatusLabel.Text = "работают потоки (Task + общая память процесса)";
            return;
        }

        StartNewRun();
    }

    private void StartNewRun()
    {
        _runner?.Stop();

        var options = new SimulationOptions
        {
            Demo = _demo,
            Technique = _technique,
            Workers = _workers,
            Accounts = _demo == 1 ? 1 : 4,
            Total = _demo == 1 ? 1000 : 10000,
            Amount = 10,
            MaxTransfer = 50,
            GapMs = (int)Math.Round(GapSlider.Value),
            WorkMicroseconds = (int)Math.Round(WorkSlider.Value),
            AggressiveConflicts = BoostSwitch.IsToggled
        };

        _runner = new SimulationRunner(options);

        ResetVisuals();
        _runner.Start();

        _paused = false;
        PauseButton.Text = "⏸ Пауза";
        StatusLabel.Text = $"{options.Workers} потоков · {DescribeTechnique(options.Technique)} · " +
                           $"пауза {options.GapMs} мс · работа {options.WorkMicroseconds} мкс";
        UpdateInvariant();
    }

    private string DescribeTechnique(Technique technique) => technique switch
    {
        Technique.Stm => "транзакции (STM)",
        Technique.Lock => "блокировки",
        Technique.Cas => "CAS-цикл",
        _ => "без синхронизации"
    };

    private void OnPauseClicked(object? sender, EventArgs e)
    {
        if (_runner is null)
            return;

        _paused = !_paused;
        _runner.Paused = _paused;
        PauseButton.Text = _paused ? "▶ Продолжить" : "⏸ Пауза";
        StatusLabel.Text = _paused ? "пауза (потоки ждут)" : "работают потоки";
    }

    private void OnResetClicked(object? sender, EventArgs e)
    {
        _runner?.Reset();
        _runner = null;
        _paused = false;
        PauseButton.Text = "⏸ Пауза";
        ResetVisuals();
        StatusLabel.Text = "сброшено: нажмите «Запустить»";
    }

    /// <summary>Привести схему, показатели и журнал к исходному виду.</summary>
    private void ResetVisuals()
    {
        int accounts = _demo == 1 ? 1 : 4;
        int total = _demo == 1 ? 1000 : 10000;

        _network.Setup(accounts, total);
        _network.Workers = _workers;
        _timeline.Workers = _workers;
        _timeline.Clear();

        for (int i = 0; i < MaxWorkers; i++)
        {
            _workerOps[i] = 0;
            _workerRetries[i] = 0;
            _workerWaits[i] = 0;
            _workerStates[i] = "пауза";
            _workerLastEvent[i] = 0;
        }

        _log.Clear();
        _filterWorker = -1;
        RenderJournal();

        OpsLabel.Text = "0";
        CommitsLabel.Text = "0";
        RetriesLabel.Text = "0";
        RefusalsLabel.Text = "0";
        RateLabel.Text = "0";
        EfficiencyLabel.Text = "—";
        _rateBaseMs = 0;
        _rateBaseOps = 0;
        _invariantBadSince = 0;

        // показываем стартовые балансы, чтобы схема не была пустой
        if (_runner is null)
        {
            var preview = new Bank(accounts, total);
            var names = new string[accounts];
            var balances = new int[accounts];
            var versions = new long[accounts];

            for (int i = 0; i < accounts; i++)
            {
                names[i] = _demo == 1 ? "Счёт" : preview.Accounts[i].Name;
                balances[i] = preview.Accounts[i].Balance;
                versions[i] = 1;
            }

            _network.Update(new ModelSnapshot
            {
                Names = names,
                Balances = balances,
                Versions = versions,
                Locked = new bool[accounts]
            });

            InvariantLabel.TextColor = Color.FromArgb("#8fa3c4");
            InvariantLabel.Text = _demo == 1
                ? $"Стартовый баланс: {total:N0} ₽"
                : $"Сумма на счетах: {total:N0} ₽ (не должна меняться)";
        }

        UpdateWorkerCards();
    }
}
