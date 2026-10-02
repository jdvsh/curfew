using Curfew.Models;

namespace Curfew.Services;

public sealed class ScheduleValidationException : Exception
{
    public ScheduleValidationException(string message) : base(message) { }
}

public static class NetUserTimeConverter
{
    /// <summary>Day codes `net user /times:` accepts, Mon-Sun to match schedule day indices.</summary>
    private static readonly string[] DayCodes = { "M", "T", "W", "Th", "F", "Sa", "Su" };

    /// <summary>
    /// Builds the value that goes after /times: in `net user NAME /times:VALUE`.
    /// Multiple blocks per day repeat the day list per block, e.g.
    /// "M-Su,3:00PM-6:00PM;M-Su,7:00PM-9:00PM". Returns "all" if nothing is allowed.
    /// </summary>
    public static string Build(ChildSchedule schedule)
    {
        var perDay = Enumerable.Range(0, 7).Select(schedule.RangesForDay).ToArray();
        if (!perDay.Any(r => r.Count > 0)) return "all";

        var segments = new List<string>();
        int i = 0;
        while (i < 7)
        {
            if (perDay[i].Count == 0) { i++; continue; }

            // Group consecutive days with identical blocks (Mon-Fri -> "M-F").
            int j = i;
            while (j + 1 < 7 && perDay[j + 1].SequenceEqual(perDay[i])) j++;

            var dayPart = j == i ? DayCodes[i] : $"{DayCodes[i]}-{DayCodes[j]}";
            foreach (var block in perDay[i])
                segments.Add($"{dayPart},{FormatTime(block.From)}-{FormatTime(block.To)}");
            i = j + 1;
        }
        return string.Join(';', segments);
    }

    private static string FormatTime(int hour24)
    {
        var h = hour24 % 24;
        var period = h < 12 ? "AM" : "PM";
        var display = h % 12;
        if (display == 0) display = 12;
        return $"{display}:00{period}";
    }
}
