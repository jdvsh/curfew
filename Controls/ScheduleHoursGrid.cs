using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Curfew.Models;

namespace Curfew.Controls;

/// <summary>
/// Weekly grid (Mon-Sun x 6am-11pm), drawn directly. Click or drag to paint hours on/off;
/// the first cell touched decides whether the stroke paints "allowed" or "blocked".
/// </summary>
public sealed class ScheduleHoursGrid : FrameworkElement
{
    private const int FirstHour = 6;
    private const int HourCount = 18;
    private const double LabelWidth = 48;
    private const double HeaderHeight = 28;
    private const double MinRowHeight = 16;
    private static readonly string[] Days = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };

    private static readonly Brush AllowedBrush = Freeze(new SolidColorBrush(Color.FromRgb(17, 73, 87)));
    private static readonly Brush BlockedBrush = Freeze(new SolidColorBrush(Color.FromRgb(217, 222, 218)));
    private static readonly Typeface Face = new("Segoe UI");

    private ChildSchedule? _schedule;
    private Point? _last;
    private bool _paintAllowed;

    public event Action? Changed;

    public ChildSchedule? Schedule
    {
        get => _schedule;
        set { _schedule = value; InvalidateVisual(); }
    }

    public ScheduleHoursGrid()
    {
        MinHeight = HeaderHeight + MinRowHeight * HourCount;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
    }

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    private bool IsAllowed(int day, int hour) =>
        _schedule is not null && _schedule.CustomHours.TryGetValue(day, out var set) && set.Contains(hour);

    // ---- drawing ----

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // makes empty areas hit-testable

        var cellW = (w - LabelWidth) / 7;
        var rowH = (h - HeaderHeight) / HourCount;

        for (var d = 0; d < 7; d++)
        {
            var text = Text(Days[d], 12, FontWeights.SemiBold);
            dc.DrawText(text, new Point(LabelWidth + d * cellW + (cellW - text.Width) / 2,
                                        (HeaderHeight - text.Height) / 2));
        }

        for (var r = 0; r < HourCount; r++)
        {
            var hour = FirstHour + r;
            var y = HeaderHeight + r * rowH;

            var label = Text($"{(hour > 12 ? hour - 12 : hour)}{(hour >= 12 ? "pm" : "am")}", 11, FontWeights.Normal);
            dc.DrawText(label, new Point(LabelWidth - 6 - label.Width, y + (rowH - label.Height) / 2));

            for (var d = 0; d < 7; d++)
            {
                var rect = new Rect(LabelWidth + d * cellW + 1, y + 1, Math.Max(cellW - 2, 0), Math.Max(rowH - 2, 0));
                dc.DrawRoundedRectangle(IsAllowed(d, hour) ? AllowedBrush : BlockedBrush, null, rect, 2, 2);
            }
        }
    }

    private FormattedText Text(string s, double size, FontWeight weight) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(Face.FontFamily, FontStyles.Normal, weight, FontStretches.Normal),
            size, SystemColors.ControlTextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    // ---- mouse handling ----

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(this);
        if (CellAt(pos) is not var (day, hour)) return;

        CaptureMouse();
        _last = pos;
        _paintAllowed = !IsAllowed(day, hour);
        PaintCell(day, hour);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!IsMouseCaptured || _last is not { } start) return;

        var end = e.GetPosition(this);
        double dx = end.X - start.X, dy = end.Y - start.Y;
        // Interpolate so fast drags don't skip cells.
        var steps = Math.Clamp((int)Math.Ceiling(Math.Sqrt(dx * dx + dy * dy) / 6), 1, 1000);
        for (var s = 1; s <= steps; s++)
        {
            var t = (double)s / steps;
            if (CellAt(new Point(start.X + dx * t, start.Y + dy * t)) is var (day, hour))
                PaintCell(day, hour);
        }
        _last = end;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _last = null;
        ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e) => _last = null;

    private (int Day, int Hour)? CellAt(Point p)
    {
        var cellW = (ActualWidth - LabelWidth) / 7;
        var rowH = (ActualHeight - HeaderHeight) / HourCount;
        if (p.X < LabelWidth || p.Y < HeaderHeight || cellW <= 0 || rowH <= 0) return null;

        var day = (int)((p.X - LabelWidth) / cellW);
        var row = (int)((p.Y - HeaderHeight) / rowH);
        if (day >= 7 || row >= HourCount) return null;
        return (day, FirstHour + row);
    }

    private void PaintCell(int day, int hour)
    {
        if (_schedule is null || IsAllowed(day, hour) == _paintAllowed) return;

        if (!_schedule.CustomHours.TryGetValue(day, out var set))
            _schedule.CustomHours[day] = set = new HashSet<int>();
        if (_paintAllowed) set.Add(hour); else set.Remove(hour);

        InvalidateVisual();
        Changed?.Invoke();
    }
}
