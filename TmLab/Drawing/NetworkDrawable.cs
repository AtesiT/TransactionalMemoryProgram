using System;
using System.Collections.Generic;
using Microsoft.Maui.Graphics;

namespace TmLab.Drawing;

/// <summary>Судьба перевода: дошёл, откатился из-за конфликта или отменён.</summary>
public enum CoinStatus
{
    /// <summary>Транзакция зафиксирована — деньги у получателя.</summary>
    Ok,

    /// <summary>Конфликт версий: транзакция откатилась, деньги не ушли.</summary>
    Conflict,

    /// <summary>Отказ: недостаточно средств.</summary>
    Refused
}

/// <summary>Снимок состояния банка для рисования (заполняется в UI-потоке).</summary>
public sealed class ModelSnapshot
{
    public string[] Names { get; init; } = Array.Empty<string>();

    public int[] Balances { get; init; } = Array.Empty<int>();

    public long[] Versions { get; init; } = Array.Empty<long>();

    public bool[] Locked { get; init; } = Array.Empty<bool>();
}

/// <summary>
/// Живая схема: участники с балансами и монетки, летящие по линиям.
///
/// Монетка долетает до получателя — транзакция зафиксирована. Монетка гаснет
/// на полпути — был конфликт и откат. Баланс получателя увеличивается ровно
/// в момент прилёта: суммы «в пути» считаются отдельно (см. _pending).
/// </summary>
public sealed class NetworkDrawable : IDrawable
{
    private const int MaxCoins = 140;

    private static readonly string[] Palette =
    {
        "#4ea1ff", "#35d07f", "#ffb020", "#a78bfa", "#ff7ab6", "#4dd6e0", "#ffd166", "#8be36b"
    };

    private sealed class Coin
    {
        public int FromIndex { get; init; } = -1;      // -1 = монетка от потока (Пример 1)
        public int WorkerId { get; init; }
        public int ToIndex { get; init; }
        public int Amount { get; init; }
        public CoinStatus Status { get; init; }
        public long StartedMs { get; init; }
        public long DurationMs { get; init; } = 900;
    }

    private sealed class Ring
    {
        public float X { get; init; }
        public float Y { get; init; }
        public long BornMs { get; init; }
    }

    private readonly List<Coin> _coins = new();
    private readonly List<Ring> _rings = new();
    private readonly Dictionary<int, int> _pending = new();

    private ModelSnapshot _snapshot = new();

    /// <summary>true — Пример 1 (потоки пополняют один счёт), false — Пример 2 (переводы).</summary>
    public bool IsDepositDemo { get; set; } = true;

    public int Workers { get; set; } = 8;

    /// <summary>Сколько анимаций не поместилось — показывается в подписи.</summary>
    public int Skipped { get; private set; }

    /// <summary>
    /// Множитель времени полёта монетки. Задаётся страницей: на медленном темпе
    /// монетки летят дольше, и каждый перевод успеваешь разглядеть.
    /// </summary>
    public double FlightScale { get; set; } = 1.0;

    public string Caption { get; set; } = string.Empty;

    public Color CaptionColor { get; set; } = Color.FromArgb("#6d80a3");

    /// <summary>Подготовить схему под новый запуск.</summary>
    public void Setup(int accountsCount, int total)
    {
        var names = new string[accountsCount];
        var balances = new int[accountsCount];
        var versions = new long[accountsCount];
        var locked = new bool[accountsCount];

        for (int i = 0; i < accountsCount; i++)
        {
            names[i] = IsDepositDemo ? "Счёт" : Models.Bank.NameOf(i);
            balances[i] = total / accountsCount;
            versions[i] = 1;
        }

        _snapshot = new ModelSnapshot
        {
            Names = names,
            Balances = balances,
            Versions = versions,
            Locked = locked
        };

        Clear();
        Skipped = 0;
    }

    public void Clear()
    {
        _coins.Clear();
        _rings.Clear();
        _pending.Clear();
    }

    /// <summary>Обновить числа из банка (вызывается в UI-потоке перед перерисовкой).</summary>
    public void Update(ModelSnapshot snapshot) => _snapshot = snapshot;

    /// <summary>Запустить анимацию перевода. from = -1 — монетка летит от потока.</summary>
    public void Send(int from, int to, int amount, CoinStatus status, int workerId)
    {
        if (_coins.Count >= MaxCoins)
        {
            Skipped++;
            return;
        }

        if (status == CoinStatus.Ok)
            _pending[to] = PendingFor(to) + amount;      // деньги «в пути» к получателю

        long now = Environment.TickCount64;
        int load = _coins.Count;

        // при нагрузке монетки летят быстрее, чтобы не забивать экран,
        // в спокойном темпе — медленнее
        double baseFlight = load > 60 ? 620 : load > 25 ? 900 : 1200;
        long flight = (long)Math.Clamp(baseFlight * FlightScale, 450, 2600);

        _coins.Add(new Coin
        {
            FromIndex = from,
            ToIndex = to,
            Amount = amount,
            Status = status,
            WorkerId = workerId,
            StartedMs = now,
            DurationMs = status == CoinStatus.Ok ? flight : (long)(flight * 0.72)
        });
    }

    private int PendingFor(int index) => _pending.TryGetValue(index, out int value) ? value : 0;

    /// <summary>Показанный баланс = реальный минус то, что ещё летит (деньги придут по прилёте).</summary>
    private int ShownBalance(int index) => _snapshot.Balances[index] - PendingFor(index);

    public void Draw(ICanvas canvas, RectF rect)
    {
        long now = Environment.TickCount64;
        int count = _snapshot.Names.Length;

        canvas.Antialias = true;

        if (count == 0)
        {
            DrawCaption(canvas, rect);      // данных ещё нет — рисуем только подпись
            return;
        }

        DrawLinks(canvas, rect, count);
        DrawWorkerDots(canvas, rect);
        DrawCoins(canvas, rect, count, now);
        DrawRings(canvas, now);
        DrawNodes(canvas, rect, count);
        DrawCaption(canvas, rect);
    }

    // -------------------------------------------------------------------------
    // Отрисовка по частям
    // -------------------------------------------------------------------------

    private void DrawLinks(ICanvas canvas, RectF rect, int count)
    {
        if (count < 2)
            return;

        canvas.StrokeColor = Color.FromArgb("#2e426e");
        canvas.StrokeSize = 1;

        for (int i = 0; i < count; i++)
        {
            for (int j = i + 1; j < count; j++)
            {
                var a = NodePosition(i, rect, count);
                var b = NodePosition(j, rect, count);
                canvas.DrawLine(a.X, a.Y, b.X, b.Y);
            }
        }
    }

    private void DrawWorkerDots(ICanvas canvas, RectF rect)
    {
        if (!IsDepositDemo)
            return;

        canvas.FontColor = Color.FromArgb("#8fa3c4");
        canvas.FontSize = 10;

        for (int worker = 0; worker < Workers; worker++)
        {
            var point = WorkerPosition(worker, rect);
            canvas.FillColor = Color.FromArgb("#4ea1ff");
            canvas.FillCircle(point.X, point.Y, 7f);
            canvas.DrawString($"#{worker}", point.X - 20, point.Y - 26, 40, 16,
                HorizontalAlignment.Center, VerticalAlignment.Center);
        }
    }

    private void DrawCoins(ICanvas canvas, RectF rect, int count, long now)
    {
        canvas.FontSize = 11;

        for (int i = _coins.Count - 1; i >= 0; i--)
        {
            var coin = _coins[i];
            double progress = (now - coin.StartedMs) / (double)Math.Max(1L, coin.DurationMs);

            if (progress >= 1)
            {
                if (coin.Status == CoinStatus.Ok)
                {
                    var target = NodePosition(coin.ToIndex, rect, count);
                    _rings.Add(new Ring { X = target.X, Y = target.Y, BornMs = now });
                    _pending[coin.ToIndex] = Math.Max(0, PendingFor(coin.ToIndex) - coin.Amount);
                }

                _coins.RemoveAt(i);
                continue;
            }

            float fade = 1f;
            double t = progress;

            if (coin.Status != CoinStatus.Ok)
            {
                t = Math.Min(progress / 0.75, 1.0) * 0.55;              // гаснет, не доходя
                fade = progress > 0.6 ? (float)Math.Max(0, 1 - (progress - 0.6) / 0.4) : 1f;
            }
            else
            {
                t = 1 - Math.Pow(1 - progress, 2);                      // мягкое долетание
            }

            var from = coin.FromIndex < 0
                ? WorkerPosition(coin.WorkerId, rect)
                : NodePosition(coin.FromIndex, rect, count);
            var to = NodePosition(coin.ToIndex, rect, count);

            // путь — квадратичная кривая с изгибом
            float midX = (from.X + to.X) / 2f;
            float midY = (from.Y + to.Y) / 2f;
            float dx = to.X - from.X;
            float dy = to.Y - from.Y;
            float length = Math.Max(1f, (float)Math.Sqrt(dx * dx + dy * dy));
            float bend = Math.Min(60f, length * 0.16f);
            float controlX = midX - dy / length * bend;
            float controlY = midY + dx / length * bend;

            float px = Quadratic(from.X, controlX, to.X, t);
            float py = Quadratic(from.Y, controlY, to.Y, t);

            var color = coin.Status switch
            {
                CoinStatus.Ok => Color.FromArgb("#35d07f"),
                CoinStatus.Refused => Color.FromArgb("#a78bfa"),
                _ => Color.FromArgb("#ff5d6c")
            };

            // след монетки
            canvas.StrokeColor = color;
            canvas.StrokeSize = 2;
            canvas.Alpha = 0.25f * fade;
            var path = new PathF();
            path.MoveTo(from.X, from.Y);
            path.QuadTo(controlX, controlY, px, py);
            canvas.DrawPath(path);

            // сама монетка
            canvas.Alpha = fade;
            canvas.FillColor = color;
            canvas.FillCircle(px, py, coin.Status == CoinStatus.Ok ? 9f : 7f);

            canvas.StrokeColor = Color.FromArgb("#070b16");
            canvas.StrokeSize = 2;
            canvas.DrawCircle(px, py, coin.Status == CoinStatus.Ok ? 9f : 7f);

            if (fade > 0.5f && _coins.Count <= 26)
            {
                canvas.FontColor = coin.Status == CoinStatus.Ok
                    ? Color.FromArgb("#d6ffe9")
                    : Color.FromArgb("#ffd6da");
                canvas.DrawString(coin.Amount.ToString("N0"), px - 40, py - 26, 80, 18,
                    HorizontalAlignment.Center, VerticalAlignment.Center);
            }

            canvas.Alpha = 1f;
        }
    }

    private static float Quadratic(float a, float control, float b, double t)
    {
        double mt = 1 - t;
        return (float)(mt * mt * a + 2 * mt * t * control + t * t * b);
    }

    private void DrawRings(ICanvas canvas, long now)
    {
        for (int i = _rings.Count - 1; i >= 0; i--)
        {
            var ring = _rings[i];
            double age = (now - ring.BornMs) / 620.0;

            if (age >= 1)
            {
                _rings.RemoveAt(i);
                continue;
            }

            canvas.Alpha = (float)(1 - age);
            canvas.StrokeColor = Color.FromArgb("#35d07f");
            canvas.StrokeSize = 2.5f;
            canvas.DrawCircle(ring.X, ring.Y, 30 + (float)age * 30);
            canvas.Alpha = 1f;
        }
    }

    private void DrawNodes(ICanvas canvas, RectF rect, int count)
    {
        bool isPerson = !IsDepositDemo;
        float radius = isPerson ? 46f : 42f;

        for (int i = 0; i < count; i++)
        {
            var center = NodePosition(i, rect, count);
            var accent = Color.FromArgb(Palette[i % Palette.Length]);
            bool locked = i < _snapshot.Locked.Length && _snapshot.Locked[i];

            // тело узла
            canvas.FillColor = Color.FromArgb("#111a30");
            canvas.FillCircle(center.X, center.Y, radius);

            canvas.StrokeColor = locked ? Color.FromArgb("#ffb020") : accent;
            canvas.StrokeSize = 3;
            canvas.DrawCircle(center.X, center.Y, radius);

            if (locked)
            {
                canvas.StrokeColor = Color.FromArgb("#ffb020");
                canvas.StrokeSize = 1.5f;
                canvas.StrokeDashPattern = new[] { 4f, 4f };
                canvas.DrawCircle(center.X, center.Y, radius + 7);
                canvas.StrokeDashPattern = null!;      // снова сплошная линия
            }

            canvas.FontColor = Color.FromArgb("#e6ecf7");
            canvas.FontSize = 13;
            canvas.DrawString(
                _snapshot.Names[i],
                center.X - radius, center.Y + 4f, radius * 2, 20,
                HorizontalAlignment.Center, VerticalAlignment.Center);

            canvas.FontColor = Color.FromArgb("#9fb2d4");
            canvas.FontSize = 11;
            canvas.DrawString(
                ShownBalance(i).ToString("N0") + " ₽",
                center.X - radius, center.Y + 22f, radius * 2, 18,
                HorizontalAlignment.Center, VerticalAlignment.Center);

            // версия данных — по ней транзакция ловит конфликт
            canvas.FontColor = Color.FromArgb("#4ea1ff");
            canvas.FontSize = 10;
            canvas.DrawString(
                "v" + (_snapshot.Versions.Length > i ? _snapshot.Versions[i] : 1),
                center.X + radius - 26, center.Y - radius + 6, 24, 14,
                HorizontalAlignment.Right, VerticalAlignment.Center);

            if (locked)
            {
                canvas.FontColor = Color.FromArgb("#ffb020");
                canvas.FontSize = 11;
                canvas.DrawString(
                    "ждёт",
                    center.X - radius + 2, center.Y - radius + 6, 40, 14,
                    HorizontalAlignment.Left, VerticalAlignment.Center);
            }
        }
    }

    private void DrawCaption(ICanvas canvas, RectF rect)
    {
        canvas.FontColor = CaptionColor;
        canvas.FontSize = 11;

        string text = Caption;
        if (!string.IsNullOrEmpty(text))
        {
            canvas.DrawString(text, rect.X + 8, rect.Bottom - 20, rect.Width - 90, 18,
                HorizontalAlignment.Left, VerticalAlignment.Center);
        }

        if (Skipped > 0)
        {
            canvas.FontColor = Color.FromArgb("#6d80a3");
            canvas.DrawString(
                $"анимаций пропущено: {Skipped}",
                rect.Right - 170, rect.Bottom - 20, 160, 18,
                HorizontalAlignment.Right, VerticalAlignment.Center);
        }
    }

    // -------------------------------------------------------------------------
    // Геометрия
    // -------------------------------------------------------------------------

    private static PointF NodePosition(int index, RectF rect, int count)
    {
        float cx = rect.Center.X;
        float cy = rect.Center.Y;

        if (count <= 1)
            return new PointF(cx, cy);

        if (count == 2)
            return new PointF(cx + (index == 0 ? -rect.Width * 0.22f : rect.Width * 0.22f), cy);

        float radius = Math.Min(rect.Width * 0.30f, rect.Height * 0.34f);
        double angle = -Math.PI / 2 + index * (2 * Math.PI / count);

        return new PointF(
            cx + (float)(Math.Cos(angle) * radius),
            cy + (float)(Math.Sin(angle) * radius * 0.92));
    }

    private PointF WorkerPosition(int workerId, RectF rect)
    {
        float cx = rect.Center.X;
        float cy = rect.Center.Y;
        float radius = Math.Min(rect.Width * 0.44f, rect.Height * 0.47f);

        int total = Math.Max(1, Workers);
        double t = total == 1 ? 0.5 : workerId / (double)(total - 1);
        double angle = Math.PI * (0.15 + 0.7 * t);

        return new PointF(
            cx + (float)(Math.Cos(angle) * radius),
            cy + (float)(Math.Sin(angle) * radius));
    }
}
