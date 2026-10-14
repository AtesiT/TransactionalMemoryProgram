using System;
using System.Collections.Generic;
using Microsoft.Maui.Graphics;

namespace TmLab.Drawing;

/// <summary>
/// Таймлайн: по дорожке на каждый поток, внутри — фазы одной попытки
/// (чтение → вычисления → валидация → коммит) и красные участки конфликтов.
///
/// Так видно разницу подходов: при транзакциях потоки работают параллельно и
/// лишь иногда «переигрывают» попытку, а при блокировках стоят в ожидании.
/// </summary>
public sealed class TimelineDrawable : IDrawable
{
    private sealed class Phase
    {
        public int WorkerId { get; init; }
        public string Kind { get; init; } = "idle";
        public long Start { get; set; }
        public long End { get; set; }
    }

    private sealed class Attempt
    {
        public long Start { get; init; }
        public long End { get; set; }
        public bool Failed { get; set; }
    }

    private readonly List<Phase> _phases = new();
    private readonly Dictionary<int, Attempt> _attempts = new();

    public int Workers { get; set; } = 8;

    /// <summary>Сколько миллисекунд помещается на экран.</summary>
    public long WindowMs { get; set; } = 9000;

    public void Clear()
    {
        _phases.Clear();
        _attempts.Clear();
    }

    public void SetWindow(long windowMs) => WindowMs = windowMs;

    /// <summary>Добавить фазу; соседние фазы того же вида сливаются, чтобы не было мельтешения.</summary>
    public void AddPhase(int workerId, string kind, long now)
    {
        if (_phases.Count > 0)
        {
            var last = _phases[^1];
            if (last.WorkerId == workerId && last.Kind == kind && now - last.End < 70)
            {
                last.End = now;
                return;
            }
        }

        _phases.Add(new Phase { WorkerId = workerId, Kind = kind, Start = now, End = now });

        // Под нагрузкой фаз становится очень много: держим примерно окно показа,
        // остальное (самое старое) выбрасываем.
        if (_phases.Count > 40000)
            _phases.RemoveRange(0, 20000);
    }

    public void AttemptBegin(int workerId, long now) =>
        _attempts[workerId] = new Attempt { Start = now, End = now };

    public void AttemptEnd(int workerId, long now, bool failed)
    {
        if (_attempts.TryGetValue(workerId, out var attempt))
        {
            attempt.End = now;
            attempt.Failed = failed;
        }
    }

    public void Draw(ICanvas canvas, RectF rect)
    {
        long now = Environment.TickCount64;
        long windowStart = now - WindowMs;

        int lanes = Math.Max(1, Workers);
        float padLeft = 34f;
        float padTop = 6f;
        float padBottom = 16f;
        float laneHeight = Math.Max(8f, (rect.Height - padTop - padBottom) / lanes);
        float barHeight = Math.Max(5f, laneHeight - 5f);
        float x0 = rect.X + padLeft;
        float x1 = rect.Right - 6f;

        canvas.Antialias = true;

        // сетка по времени
        canvas.FontColor = Color.FromArgb("#6d80a3");
        canvas.FontSize = 10;
        canvas.StrokeColor = Color.FromArgb("#22304f");
        canvas.StrokeSize = 1;

        // Отметки времени: считаем от «сейчас» назад, поэтому подписи читаются
        // как «секунду назад», «две назад» и так далее.
        long step = WindowMs >= 12000 ? 2000 : 1000;
        for (long offset = step; offset <= WindowMs; offset += step)
        {
            float x = MapX(now - offset, windowStart, x0, x1);
            canvas.DrawLine(x, rect.Y + padTop, x, rect.Bottom - padBottom);

            string label = "-" + offset / 1000 + "s";
            canvas.DrawString(label, x + 2, rect.Bottom - padBottom, 44, 14,
                HorizontalAlignment.Left, VerticalAlignment.Center);
        }

        // дорожки потоков
        for (int worker = 0; worker < lanes; worker++)
        {
            float y = rect.Y + padTop + worker * laneHeight;

            canvas.FillColor = Color.FromArgb("#16213c");
            canvas.FillRectangle(x0, y + 2, x1 - x0, barHeight);

            canvas.FontColor = Color.FromArgb("#8fa3c4");
            canvas.FontSize = 10;
            canvas.DrawString($"#{worker}", rect.X + 2, y, 30, barHeight,
                HorizontalAlignment.Left, VerticalAlignment.Center);

            // рамка последней попытки
            if (_attempts.TryGetValue(worker, out var attempt))
            {
                float ax = MapX(attempt.Start, windowStart, x0, x1);
                float aw = Math.Max(2f, MapX(attempt.End, windowStart, x0, x1) - ax);

                canvas.StrokeColor = attempt.Failed
                    ? Color.FromArgb("#ff5d6c")
                    : Color.FromArgb("#8fa3c4");
                canvas.StrokeSize = 1;
                canvas.DrawRectangle(ax, y + 1.5f, aw, barHeight + 1);
            }
        }

        // фазы
        foreach (var phase in _phases)
        {
            if (phase.End < windowStart || phase.WorkerId >= lanes)
                continue;

            float y = rect.Y + padTop + phase.WorkerId * laneHeight;
            float px = MapX(phase.Start, windowStart, x0, x1);
            float pw = Math.Max(1.6f, MapX(phase.End, windowStart, x0, x1) - px);

            canvas.FillColor = ColorFor(phase.Kind);
            canvas.FillRectangle(px, y + 3, pw, barHeight - 2);
        }

        PruneOldPhases(windowStart);
    }

    /// <summary>Перевести время события в координату X внутри окна таймлайна.</summary>
    private float MapX(long time, long windowStart, float x0, float x1)
    {
        double span = Math.Max(1, WindowMs);
        double ratio = (time - windowStart) / span;

        if (ratio < 0) ratio = 0;
        if (ratio > 1) ratio = 1;

        return x0 + (float)(ratio * (x1 - x0));
    }

    private void PruneOldPhases(long windowStart)
    {
        int remove = 0;
        while (remove < _phases.Count && _phases[remove].End < windowStart - 1000)
            remove++;

        if (remove > 0)
            _phases.RemoveRange(0, remove);
    }

    private static Color ColorFor(string kind) => kind switch
    {
        "read" => Color.FromArgb("#4ea1ff"),
        "compute" => Color.FromArgb("#58c0ff"),
        "validate" => Color.FromArgb("#a78bfa"),
        "commit" => Color.FromArgb("#35d07f"),
        "conflict" => Color.FromArgb("#ff5d6c"),
        "wait" => Color.FromArgb("#ffb020"),
        _ => Color.FromArgb("#2a3550")
    };
}
