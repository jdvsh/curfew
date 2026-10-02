using System.Text.Json;

namespace Curfew.Models;

/// <summary>Days are indexed 0=Mon .. 6=Sun to match the weekly grid UI.</summary>
public enum ScheduleMode { SameEveryDay, DifferentEachDay }

/// <summary>
/// An allowed window on a single day, in whole hours (0-24, 24 = midnight end
/// of day). <see cref="To"/> is exclusive: From=16,To=20 means "4pm-8pm".
/// </summary>
public readonly record struct TimeRange(int From, int To)
{
    public bool IsEmpty => To <= From;
    public int Hours => IsEmpty ? 0 : To - From;
}

public sealed class ChildSchedule
{
    /// <summary>Maps to a Windows local account.</summary>
    public string Username { get; }

    public ScheduleMode Mode { get; set; } = ScheduleMode.SameEveryDay;

    /// <summary>Used when Mode == SameEveryDay. A single block by design.</summary>
    public TimeRange SameRange { get; set; } = new(16, 20);

    /// <summary>
    /// Used when Mode == DifferentEachDay. Day -> individually allowed hours
    /// (not necessarily contiguous). Missing key or empty set = not allowed.
    /// </summary>
    public Dictionary<int, HashSet<int>> CustomHours { get; } = new();

    public ChildSchedule(string username) => Username = username;

    /// <summary>True if a custom per-day schedule has been painted.</summary>
    public bool HasCustomData => CustomHours.Values.Any(s => s.Count > 0);

    /// <summary>Effective allowed blocks for day [d] (0=Mon..6=Sun), regardless of mode.</summary>
    public List<TimeRange> RangesForDay(int d)
    {
        if (Mode == ScheduleMode.SameEveryDay)
            return SameRange.IsEmpty ? new() : new() { SameRange };
        return MergeHours(CustomHours.TryGetValue(d, out var set) ? set : new HashSet<int>());
    }

    public int TotalWeeklyHours =>
        Enumerable.Range(0, 7).Sum(d => RangesForDay(d).Sum(r => r.Hours));

    private static List<TimeRange> MergeHours(HashSet<int> hours)
    {
        var ranges = new List<TimeRange>();
        if (hours.Count == 0) return ranges;

        var sorted = hours.OrderBy(h => h).ToList();
        int start = sorted[0], prev = sorted[0];
        foreach (var h in sorted.Skip(1))
        {
            if (h == prev + 1) { prev = h; continue; }
            ranges.Add(new TimeRange(start, prev + 1));
            start = prev = h;
        }
        ranges.Add(new TimeRange(start, prev + 1));
        return ranges;
    }

    // ---- persistence ----

    internal sealed class Dto
    {
        public string Username { get; set; } = "";
        public string Mode { get; set; } = nameof(ScheduleMode.SameEveryDay);
        public int SameFrom { get; set; } = 16;
        public int SameTo { get; set; } = 20;
        public Dictionary<string, List<int>> CustomHours { get; set; } = new();
    }

    public string ToJson() => JsonSerializer.Serialize(new Dto
    {
        Username = Username,
        Mode = Mode.ToString(),
        SameFrom = SameRange.From,
        SameTo = SameRange.To,
        CustomHours = CustomHours.ToDictionary(
            e => e.Key.ToString(),
            e => e.Value.OrderBy(h => h).ToList()),
    });

    public static ChildSchedule FromJson(string json)
    {
        var dto = JsonSerializer.Deserialize<Dto>(json) ?? throw new JsonException("Empty schedule.");
        var schedule = new ChildSchedule(dto.Username)
        {
            Mode = Enum.Parse<ScheduleMode>(dto.Mode),
            SameRange = new TimeRange(dto.SameFrom, dto.SameTo),
        };
        foreach (var (key, hours) in dto.CustomHours)
        {
            if (!int.TryParse(key, out var day) || day < 0 || day > 6) continue;
            schedule.CustomHours[day] = hours.Where(h => h is >= 0 and < 24).ToHashSet();
        }
        return schedule;
    }
}
