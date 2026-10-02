using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Curfew.Models;
using System.IO;

namespace Curfew.Services;

public sealed record ProcessOutput(int ExitCode, string StdOut, string StdErr);

public sealed class WindowsScheduleService
{
    private readonly Func<IReadOnlyList<string>, Task<ProcessOutput>> _runNet;
    private readonly Func<IReadOnlyList<string>, Task<ProcessOutput>> _runSchtasks;
    private readonly string? _currentUsername;
    private readonly string? _programDataDirectory;

    private const string EnforceTaskName = "CurfewEnforce";

    /// <summary>
    /// Runs every minute as SYSTEM. Reads policy.json (allowed hours per day per
    /// account, 0=Mon..6=Sun) and logs off any restricted account that has a
    /// session outside its allowed hours.
    /// </summary>
    private const string EnforceScript = """
$dir = Join-Path $env:ProgramData 'Curfew'
$logPath = Join-Path $dir 'logoff.log'
function Write-Log($message) {
  Add-Content -LiteralPath $logPath -Value "$(Get-Date -Format o) $message"
}
try {
  $policyPath = Join-Path $dir 'policy.json'
  if (-not (Test-Path -LiteralPath $policyPath)) { exit 0 }
  $policy = Get-Content -LiteralPath $policyPath -Raw -Encoding UTF8 | ConvertFrom-Json
  $now = Get-Date
  $day = [string]((([int]$now.DayOfWeek) + 6) % 7)   # 0=Mon..6=Sun
  $hour = $now.Hour

  # Map each signed-in account to its session ids using process owners.
  # quser.exe is not available on Windows Home, so it is not used.
  $sessions = @{}
  Get-Process -IncludeUserName -ErrorAction SilentlyContinue |
    Where-Object { $_.SessionId -ne 0 -and $_.UserName } |
    ForEach-Object {
      $name = ($_.UserName -split '\\')[-1].ToLower()
      if (-not $sessions.ContainsKey($name)) { $sessions[$name] = @{} }
      $sessions[$name][[int]$_.SessionId] = $true
    }

  $wtsLoaded = $false
  foreach ($entry in $policy.PSObject.Properties) {
    $account = $entry.Name
    if (-not $sessions.ContainsKey($account)) { continue }

    $dayProp = $entry.Value.PSObject.Properties[$day]
    $allowed = @()
    if ($null -ne $dayProp) { $allowed = @($dayProp.Value) }
    if ($allowed -contains $hour) { continue }

    if (-not $wtsLoaded) {
      Add-Type -Namespace Curfew -Name Wts -MemberDefinition @'
[DllImport("wtsapi32.dll", SetLastError = true)]
public static extern bool WTSLogoffSession(IntPtr hServer, int sessionId, bool bWait);
'@
      $wtsLoaded = $true
    }
    foreach ($id in @($sessions[$account].Keys)) {
      $ok = [Curfew.Wts]::WTSLogoffSession([IntPtr]::Zero, [int]$id, $false)
      Write-Log "logoff $account session $id (hour $hour not allowed): $ok"
    }
  }
} catch {
  Write-Log "enforce failed: $_"
}
""";

    public WindowsScheduleService(
        Func<IReadOnlyList<string>, Task<ProcessOutput>>? runNet = null,
        Func<IReadOnlyList<string>, Task<ProcessOutput>>? runSchtasks = null,
        string? currentUsername = null,
        string? programDataDirectory = null)
    {
        _runNet = runNet ?? (args => Exec("net", args));
        _runSchtasks = runSchtasks ?? (args => Exec("schtasks", args));
        _currentUsername = currentUsername ?? Environment.UserName;
        _programDataDirectory = programDataDirectory ?? Environment.GetEnvironmentVariable("ProgramData");
    }

    private static async Task<ProcessOutput> Exec(string file, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {file}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessOutput(process.ExitCode, await stdout, await stderr);
    }

    /// <summary>
    /// Applies <paramref name="schedule"/> to the matching Windows local account.
    /// Requires the app to run elevated (see app.manifest).
    /// </summary>
    public async Task ApplyScheduleAsync(ChildSchedule schedule)
    {
        var isCurrent = IsCurrentAccount(schedule.Username);
        var timeArg = isCurrent ? "all" : NetUserTimeConverter.Build(schedule);

        await RunNetAsync("user", schedule.Username, "/active:yes");
        await RunNetAsync("user", schedule.Username, $"/times:{timeArg}");
        await RefreshLogoffTasksAsync(schedule, enabled: !isCurrent);
    }

    private async Task RefreshLogoffTasksAsync(ChildSchedule schedule, bool enabled)
    {
        await DeleteLegacyLogoffTasksAsync(schedule.Username);
        await WritePolicyAsync(schedule, enabled);
        await EnsureEnforceTaskAsync();
    }

    /// <summary>Removes the old one-shot per-end-time tasks from earlier versions.</summary>
    private async Task DeleteLegacyLogoffTasksAsync(string username)
    {
        var prefix = TaskPrefix(username);
        var queryArgs = new[] { "/query", "/FO", "CSV", "/NH" };
        var existing = await _runSchtasks(queryArgs);
        if (existing.ExitCode != 0)
            throw new InvalidOperationException(
                $"Could not inspect existing Curfew logoff tasks: {existing.StdErr}");

        foreach (var taskName in ReadTaskNames(existing.StdOut))
        {
            var normalized = Regex.Replace(taskName, @"^\\+", "");
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var args = new[] { "/delete", "/TN", taskName, "/F" };
                CheckSchtasks(await _runSchtasks(args));
            }
        }
    }

    private static string TaskPrefix(string username)
    {
        var encoded = string.Concat(username.ToLowerInvariant().Select(c => ((int)c).ToString("x4")));
        return $"CurfewLogoff-{encoded}-";
    }

    private string DataDirectory()
    {
        if (string.IsNullOrEmpty(_programDataDirectory))
            throw new InvalidOperationException("The Windows ProgramData directory is unavailable.");
        var dir = Path.Combine(_programDataDirectory, "Curfew");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Records allowed hours per day in policy.json, which the enforcement script
    /// reads every minute. Accounts with no allowed hours are unrestricted, so
    /// they are removed from the policy.
    /// </summary>
    private async Task WritePolicyAsync(ChildSchedule schedule, bool enabled)
    {
        var path = Path.Combine(DataDirectory(), "policy.json");

        var policy = new Dictionary<string, Dictionary<string, List<int>>>();
        if (File.Exists(path))
        {
            try
            {
                policy = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, List<int>>>>(
                    await File.ReadAllTextAsync(path)) ?? new();
            }
            catch (JsonException) { policy = new(); }
        }

        var days = Enumerable.Range(0, 7).ToDictionary(
            d => d.ToString(),
            d => schedule.RangesForDay(d)
                .SelectMany(r => Enumerable.Range(r.From, r.To - r.From))
                .ToList());

        var key = schedule.Username.ToLowerInvariant();
        if (enabled && days.Values.Any(h => h.Count > 0)) policy[key] = days;
        else policy.Remove(key);

        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(policy));
        File.Move(temp, path, overwrite: true);
    }

    private async Task EnsureEnforceTaskAsync()
    {
        var dir = DataDirectory();

        var scriptPath = Path.GetFullPath(Path.Combine(dir, "enforce.ps1"));
        await File.WriteAllTextAsync(scriptPath, EnforceScript);

        // schtasks only accepts battery/catch-up settings via XML, written as UTF-16 LE with BOM.
        var xmlPath = Path.GetFullPath(Path.Combine(dir, "enforce.xml"));
        await File.WriteAllTextAsync(xmlPath, EnforceTaskXml(scriptPath), Encoding.Unicode);

        CheckSchtasks(await _runSchtasks(new[] { "/create", "/TN", EnforceTaskName, "/XML", xmlPath, "/F" }));
    }

    private static string EnforceTaskXml(string scriptPath)
    {
        var escaped = scriptPath.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <Triggers>
    <TimeTrigger>
      <Repetition>
        <Interval>PT1M</Interval>
        <StopAtDurationEnd>false</StopAtDurationEnd>
      </Repetition>
      <StartBoundary>2020-01-01T00:00:00</StartBoundary>
      <Enabled>true</Enabled>
    </TimeTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>S-1-5-18</UserId>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <StartWhenAvailable>true</StartWhenAvailable>
    <ExecutionTimeLimit>PT2M</ExecutionTimeLimit>
    <Hidden>true</Hidden>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>powershell.exe</Command>
      <Arguments>-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "{escaped}"</Arguments>
    </Exec>
  </Actions>
</Task>
""";
    }

    private static IEnumerable<string> ReadTaskNames(string output) =>
        Regex.Split(output, @"\r?\n")
            .Where(line => line.StartsWith('"'))
            .Select(line => Regex.Match(line, "^\"((?:\"\"|[^\"])*)\""))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value.Replace("\"\"", "\""));

    private static void CheckSchtasks(ProcessOutput result)
    {
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Could not configure automatic logoff: {result.StdErr}");
    }

    /// <summary>
    /// Local account usernames for the sidebar. Built-in accounts and the account
    /// running this process are excluded.
    /// </summary>
    public async Task<List<string>> ListLocalAccountsAsync()
    {
        var result = await _runNet(new[] { "user" });
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"net user failed: {result.StdErr}");
        return ParseLocalAccounts(result.StdOut, _currentUsername);
    }

    public static List<string> ParseLocalAccounts(string output, string? currentUsername = null)
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "administrator", "defaultaccount", "guest", "wdagutilityaccount" };
        if (currentUsername is not null) excluded.Add(currentUsername.Trim());

        var accounts = new HashSet<string>();
        foreach (var line in output.Split('\n'))
        {
            // `net user` prints names in whitespace-separated columns between a
            // header/dashes line and a footer line.
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('-') ||
                trimmed.Contains("user accounts", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("command completed", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var name in Regex.Split(trimmed, @"\s{2,}"))
            {
                var n = name.Trim();
                if (n.Length > 0 && !excluded.Contains(n)) accounts.Add(n);
            }
        }
        return accounts.OrderBy(a => a, StringComparer.Ordinal).ToList();
    }

    private bool IsCurrentAccount(string username) =>
        _currentUsername is not null &&
        string.Equals(username.Trim(), _currentUsername.Trim(), StringComparison.OrdinalIgnoreCase);

    private async Task RunNetAsync(params string[] args)
    {
        var result = await _runNet(args);
        if (result.ExitCode != 0)
        {
            var denied = (result.StdErr + result.StdOut).Contains("System error 5");
            throw new InvalidOperationException(
                $"net {string.Join(' ', args)} failed: {result.StdErr}" +
                (denied ? " (likely needs Administrator privileges)" : ""));
        }
    }
}
