using System.Text.Json;
using Curfew.Models;
using System.IO;

namespace Curfew.Services;

/// <summary>Persists each child's schedule as JSON under %LocalAppData%\Curfew\schedules.</summary>
public sealed class ScheduleStore
{
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Curfew", "schedules");

    public async Task<ChildSchedule?> LoadAsync(string username)
    {
        var path = PathFor(username);
        if (!File.Exists(path)) return null;
        try
        {
            return ChildSchedule.FromJson(await File.ReadAllTextAsync(path));
        }
        catch (Exception e) when (e is JsonException or ArgumentException or NotSupportedException)
        {
            return null; // corrupt file -> fall back to a fresh schedule
        }
    }

    public async Task SaveAsync(ChildSchedule schedule)
    {
        Directory.CreateDirectory(Root);
        await File.WriteAllTextAsync(PathFor(schedule.Username), schedule.ToJson());
    }

    private static string PathFor(string username) =>
        Path.Combine(Root, Uri.EscapeDataString(username.ToLowerInvariant()) + ".json");
}
