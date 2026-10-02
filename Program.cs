using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

class Program
{
    // On non-Windows systems nothing is executed; Windows commands are only printed.
    public static readonly bool DryRun = !OperatingSystem.IsWindows();

    private const string ScheduleUrl =
        "https://raw.githubusercontent.com/digimezzo/scheduling/main/schedule.json";

    // Fixed on purpose: standard users can change the Windows time zone setting.
    private const string ScheduleTimeZoneId = "Europe/Brussels";

    private static readonly TimeSpan CountdownDuration = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan WarningLeadTime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaxClockDrift = TimeSpan.FromMinutes(5);

    // Difference between trusted time (server or validated clock) and the PC's UTC clock.
    private static TimeSpan clockOffset = TimeSpan.Zero;

    static DateTime TrustedUtcNow => DateTime.UtcNow + clockOffset;

    static async Task Main()
    {
        try
        {
            SecureStorage.Initialize();
        }
        catch (Exception ex)
        {
            Logger.Log($"Data folder could not be secured: {ex.Message}");
            EnforceShutdown("Data folder could not be secured");
            return;
        }

        Logger.Log("Program started");

        try
        {
            TimeZoneInfo timeZone = TimeZoneInfo.FindSystemTimeZoneById(ScheduleTimeZoneId);

            (Dictionary<string, List<TimeWindow>> schedule, DateTime? serverUtc) = await GetScheduleAsync();

            DateTime? nowUtc = ResolveTrustedUtc(serverUtc);
            if (nowUtc == null)
            {
                EnforceShutdown("Clock tampering detected");
                return;
            }

            DateTime nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc.Value, timeZone);
            TimeSpan now = nowLocal.TimeOfDay;
            string today = nowLocal.DayOfWeek.ToString();

            Logger.Log($"Time OK: {nowLocal:yyyy-MM-dd HH:mm:ss} {timeZone.Id} (Today={today})");

            if (!schedule.TryGetValue(today, out List<TimeWindow>? windows) || windows == null || windows.Count == 0)
            {
                EnforceShutdown($"No schedule entry for {today}");
                return;
            }

            TimeSpan? activeEnd = null;
            foreach (var window in windows)
            {
                if (string.IsNullOrWhiteSpace(window.Start) || string.IsNullOrWhiteSpace(window.End))
                    continue;

                TimeSpan start = TimeSpan.Parse(window.Start, CultureInfo.InvariantCulture);
                TimeSpan end = TimeSpan.Parse(window.End, CultureInfo.InvariantCulture);

                Logger.Log($"Allowed window: {start} --> {end}, now={now}");

                if (now >= start && now <= end)
                {
                    activeEnd = end;
                    break;
                }
            }

            if (activeEnd == null)
            {
                EnforceShutdown("Outside allowed window");
                return;
            }

            Logger.Log("Within allowed window");

            TimeSpan remaining = activeEnd.Value - now;
            if (remaining <= WarningLeadTime)
            {
                DateTime endLocal = nowLocal.Date + activeEnd.Value;
                WarnUser($"This PC will shut down at {endLocal:HH:mm}. Please save your work.");

                // Stay alive until the window ends; the next 5-minute run could otherwise be too late.
                DateTime countdownStartUtc = nowUtc.Value + remaining - CountdownDuration;
                while (TrustedUtcNow < countdownStartUtc)
                    Thread.Sleep(TimeSpan.FromSeconds(5));

                EnforceShutdown("Allowed window ended");
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Unhandled exception: {ex}");
            EnforceShutdown("Unexpected error");
        }
    }

    static async Task<(Dictionary<string, List<TimeWindow>>, DateTime?)> GetScheduleAsync()
    {
        const int maxAttempts = 2;

        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                Logger.Log($"Attempt {attempt}: fetching schedule");

                using HttpResponseMessage response = await client.GetAsync(ScheduleUrl);
                response.EnsureSuccessStatusCode();

                string scheduleJson = await response.Content.ReadAsStringAsync();
                var schedule = ParseScheduleJson(scheduleJson);
                SecureStorage.WriteText(SecureStorage.ScheduleCacheFile, scheduleJson);

                DateTime? serverUtc = response.Headers.Date?.UtcDateTime;
                Logger.Log($"Schedule OK ({schedule.Count} days), server time: {serverUtc:O}");

                return (schedule, serverUtc);
            }
            catch (Exception ex)
            {
                Logger.Log($"Attempt {attempt} failed: {ex.Message}");

                if (attempt < maxAttempts)
                    await Task.Delay(TimeSpan.FromSeconds(10));
            }
        }

        string? cachedJson = SecureStorage.ReadText(SecureStorage.ScheduleCacheFile);
        if (cachedJson == null)
            throw new Exception("Schedule unavailable online and no cached schedule exists");

        Logger.Log("Offline: using cached schedule");
        return (ParseScheduleJson(cachedJson), null);
    }

    static DateTime? ResolveTrustedUtc(DateTime? serverUtc)
    {
        DateTime clockUtc = DateTime.UtcNow;
        DateTime? lastSeenUtc = SecureStorage.ReadLastSeenUtc();
        DateTime trustedUtc;
        DateTime newLastSeenUtc;

        if (serverUtc != null)
        {
            if ((serverUtc.Value - clockUtc).Duration() > MaxClockDrift)
                Logger.Log($"PC clock differs from server time: clock={clockUtc:O}, server={serverUtc:O}");

            trustedUtc = serverUtc.Value;
            newLastSeenUtc = trustedUtc;
        }
        else
        {
            if (lastSeenUtc != null && clockUtc < lastSeenUtc.Value - MaxClockDrift)
            {
                Logger.Log($"TAMPERING: clock {clockUtc:O} is earlier than last seen time {lastSeenUtc:O}");
                return null;
            }

            trustedUtc = clockUtc;
            newLastSeenUtc = lastSeenUtc != null && lastSeenUtc.Value > clockUtc ? lastSeenUtc.Value : clockUtc;
        }

        clockOffset = trustedUtc - clockUtc;
        SecureStorage.WriteLastSeenUtc(newLastSeenUtc);

        return trustedUtc;
    }

    static Dictionary<string, List<TimeWindow>> ParseScheduleJson(string rawJson)
    {
        foreach (var candidate in GetJsonCandidates(rawJson))
        {
            if (TryParseSchedule(candidate, out var schedule))
                return schedule;
        }

        if (TryParseLooseDayMap(rawJson, out var looseSchedule))
            return looseSchedule;

        string preview = (rawJson ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
        if (preview.Length > 160)
            preview = preview[..160] + "...";

        throw new Exception($"Invalid schedule JSON. Preview: {preview}");
    }

    static IEnumerable<string> GetJsonCandidates(string rawJson)
    {
        string value = (rawJson ?? string.Empty).Trim();

        if (!string.IsNullOrEmpty(value) && value[0] == '\uFEFF')
            value = value[1..].TrimStart();

        yield return value;

        // Some sources return only object members (e.g. "Monday": [...]) without braces.
        if (LooksLikeObjectMembers(value))
            yield return $"{{\n{value}\n}}";

        // Some hosts return fenced markdown snippets.
        if (value.StartsWith("```", StringComparison.Ordinal))
        {
            var lines = value.Split('\n');
            if (lines.Length >= 3)
            {
                string inner = string.Join('\n', lines.Skip(1).Take(lines.Length - 2)).Trim();
                if (!string.Equals(inner, value, StringComparison.Ordinal))
                    yield return inner;
            }
        }

        int firstBrace = value.IndexOf('{');
        int lastBrace = value.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            string slice = value.Substring(firstBrace, lastBrace - firstBrace + 1);
            if (!string.Equals(slice, value, StringComparison.Ordinal))
                yield return slice;
        }

        int firstBracket = value.IndexOf('[');
        int lastBracket = value.LastIndexOf(']');
        if (firstBracket >= 0 && lastBracket > firstBracket)
        {
            string slice = value.Substring(firstBracket, lastBracket - firstBracket + 1);
            if (!string.Equals(slice, value, StringComparison.Ordinal))
                yield return slice;
        }
    }

    static bool LooksLikeObjectMembers(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string trimmed = value.TrimStart();

        if (trimmed.StartsWith("{", StringComparison.Ordinal) ||
            trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            return false;
        }

        int firstColon = trimmed.IndexOf(':');
        int firstQuote = trimmed.IndexOf('"');

        return firstQuote >= 0 && firstColon > firstQuote;
    }

    static bool TryParseSchedule(string json, out Dictionary<string, List<TimeWindow>> schedule)
    {
        schedule = new Dictionary<string, List<TimeWindow>>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (TryParseDayMap(root, out schedule))
                    return schedule.Count > 0;

                if (root.TryGetProperty("schedule", out var scheduleNode) &&
                    scheduleNode.ValueKind == JsonValueKind.Object &&
                    TryParseDayMap(scheduleNode, out schedule))
                {
                    return schedule.Count > 0;
                }

                if (root.TryGetProperty("days", out var daysNode) &&
                    daysNode.ValueKind == JsonValueKind.Object &&
                    TryParseDayMap(daysNode, out schedule))
                {
                    return schedule.Count > 0;
                }
            }

            if (root.ValueKind == JsonValueKind.Array)
            {
                if (TryParseFlatEntries(root, out schedule))
                    return schedule.Count > 0;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    static bool TryParseDayMap(JsonElement root, out Dictionary<string, List<TimeWindow>> schedule)
    {
        schedule = new Dictionary<string, List<TimeWindow>>(StringComparer.OrdinalIgnoreCase);

        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Array)
                continue;

            var windows = new List<TimeWindow>();
            foreach (var entry in prop.Value.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;

                if (!entry.TryGetProperty("start", out var startNode) ||
                    !entry.TryGetProperty("end", out var endNode))
                {
                    continue;
                }

                string? start = startNode.GetString();
                string? end = endNode.GetString();

                if (string.IsNullOrWhiteSpace(start) || string.IsNullOrWhiteSpace(end))
                    continue;

                windows.Add(new TimeWindow { Start = start, End = end });
            }

            if (windows.Count > 0)
                schedule[prop.Name] = windows;
        }

        return schedule.Count > 0;
    }

    static bool TryParseFlatEntries(JsonElement root, out Dictionary<string, List<TimeWindow>> schedule)
    {
        schedule = new Dictionary<string, List<TimeWindow>>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            if (!item.TryGetProperty("day", out var dayNode) ||
                !item.TryGetProperty("start", out var startNode) ||
                !item.TryGetProperty("end", out var endNode))
            {
                continue;
            }

            string? day = dayNode.GetString();
            string? start = startNode.GetString();
            string? end = endNode.GetString();

            if (string.IsNullOrWhiteSpace(day) || string.IsNullOrWhiteSpace(start) || string.IsNullOrWhiteSpace(end))
                continue;

            if (!schedule.TryGetValue(day, out var windows))
            {
                windows = new List<TimeWindow>();
                schedule[day] = windows;
            }

            windows.Add(new TimeWindow { Start = start, End = end });
        }

        return schedule.Count > 0;
    }

    static bool TryParseLooseDayMap(string rawJson, out Dictionary<string, List<TimeWindow>> schedule)
    {
        schedule = new Dictionary<string, List<TimeWindow>>(StringComparer.OrdinalIgnoreCase);

        string text = (rawJson ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
            return false;

        int pos = 0;
        while (pos < text.Length)
        {
            int keyStart = text.IndexOf('"', pos);
            if (keyStart < 0)
                break;

            int keyEnd = FindStringEnd(text, keyStart);
            if (keyEnd < 0)
                break;

            string key = text.Substring(keyStart + 1, keyEnd - keyStart - 1).Trim();
            int colon = SkipWhitespace(text, keyEnd + 1);
            if (colon >= text.Length || text[colon] != ':')
            {
                pos = keyEnd + 1;
                continue;
            }

            int valueStart = SkipWhitespace(text, colon + 1);
            if (valueStart >= text.Length || text[valueStart] != '[')
            {
                pos = keyEnd + 1;
                continue;
            }

            if (!TryFindMatchingBracket(text, valueStart, out int valueEnd))
            {
                pos = keyEnd + 1;
                continue;
            }

            string arrayJson = text.Substring(valueStart, valueEnd - valueStart + 1);
            if (TryParseWindowArray(arrayJson, out var windows) && windows.Count > 0)
                schedule[key] = windows;

            pos = valueEnd + 1;
        }

        return schedule.Count > 0;
    }

    static bool TryParseWindowArray(string arrayJson, out List<TimeWindow> windows)
    {
        windows = new List<TimeWindow>();

        try
        {
            using var doc = JsonDocument.Parse(arrayJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;

                if (!entry.TryGetProperty("start", out var startNode) ||
                    !entry.TryGetProperty("end", out var endNode))
                {
                    continue;
                }

                string? start = startNode.GetString();
                string? end = endNode.GetString();

                if (string.IsNullOrWhiteSpace(start) || string.IsNullOrWhiteSpace(end))
                    continue;

                windows.Add(new TimeWindow { Start = start, End = end });
            }

            return windows.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    static int SkipWhitespace(string text, int index)
    {
        int i = index;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
            i++;

        return i;
    }

    static int FindStringEnd(string text, int openingQuoteIndex)
    {
        bool escaped = false;

        for (int i = openingQuoteIndex + 1; i < text.Length; i++)
        {
            char c = text[i];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (c == '\\')
            {
                escaped = true;
                continue;
            }

            if (c == '"')
                return i;
        }

        return -1;
    }

    static bool TryFindMatchingBracket(string text, int openingBracketIndex, out int closingBracketIndex)
    {
        int depth = 0;
        bool inString = false;
        bool escaped = false;

        for (int i = openingBracketIndex; i < text.Length; i++)
        {
            char c = text[i];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (c == '"')
                    inString = false;

                continue;
            }

            if (c == '"')
            {
                inString = true;
                continue;
            }

            if (c == '[')
            {
                depth++;
                continue;
            }

            if (c == ']')
            {
                depth--;
                if (depth == 0)
                {
                    closingBracketIndex = i;
                    return true;
                }
            }
        }

        closingBracketIndex = -1;
        return false;
    }


    static void EnforceShutdown(string reason)
    {
        Logger.Log("SHUTDOWN: " + reason);

        // Clears any shutdown scheduled by the user, which would otherwise block ours (error 1190).
        RunShutdown("/a");

        int seconds = (int)CountdownDuration.TotalSeconds;
        int exitCode = RunShutdown("/s", "/f", "/t", seconds.ToString(CultureInfo.InvariantCulture),
            "/c", $"This PC will shut down in 1 minute. Save your work now. Reason: {reason}");
        Logger.Log($"Countdown shutdown scheduled (exit code {exitCode})");

        // UtcNow is unaffected by time zone changes and keeps advancing during sleep.
        DateTime deadlineUtc = DateTime.UtcNow + CountdownDuration + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadlineUtc)
            Thread.Sleep(TimeSpan.FromSeconds(2));

        // Still running means the countdown was aborted or failed: force it.
        while (true)
        {
            RunShutdown("/a");
            exitCode = RunShutdown("/s", "/f", "/t", "0");
            Logger.Log($"Immediate shutdown issued (exit code {exitCode})");

            if (DryRun)
            {
                Logger.Log("[DRY RUN] On Windows the immediate shutdown is repeated every 15 s until the PC is off");
                return;
            }

            Thread.Sleep(TimeSpan.FromSeconds(15));
        }
    }

    static int RunShutdown(params string[] arguments)
    {
        if (DryRun)
        {
            string shown = string.Join(' ', arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
            Logger.Log($@"[DRY RUN] Would run: C:\Windows\System32\shutdown.exe {shown}");
            return 0;
        }

        try
        {
            var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"))
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };

            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using Process process = Process.Start(startInfo)
                ?? throw new Exception("Process.Start returned null");

            if (!process.WaitForExit(30_000))
                return -1;

            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Logger.Log($"shutdown.exe {string.Join(' ', arguments)} failed: {ex.Message}");
            return -1;
        }
    }

    static void WarnUser(string message)
    {
        if (DryRun)
        {
            Logger.Log($"[DRY RUN] Would show popup in the active session: {message}");
            return;
        }

        try
        {
            int sessionId = WTSGetActiveConsoleSessionId();
            if (sessionId == -1)
                return;

            const string title = "PcTimeGuard";
            const int MB_ICONWARNING = 0x30;
            const int MB_SETFOREGROUND = 0x10000;
            const int MB_TOPMOST = 0x40000;

            bool sent = WTSSendMessage(IntPtr.Zero, sessionId,
                title, title.Length * sizeof(char),
                message, message.Length * sizeof(char),
                MB_ICONWARNING | MB_SETFOREGROUND | MB_TOPMOST,
                0, out _, false);

            Logger.Log($"Warning sent to session {sessionId} (success={sent}): {message}");
        }
        catch (Exception ex)
        {
            Logger.Log($"Warning failed: {ex.Message}");
        }
    }

    [DllImport("kernel32.dll")]
    private static extern int WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSSendMessage(
        IntPtr hServer, int sessionId,
        string title, int titleLength,
        string message, int messageLength,
        int style, int timeout, out int response, bool wait);
}

class TimeWindow
{
    [JsonPropertyName("start")]
    public required string Start { get; set; }

    [JsonPropertyName("end")]
    public required string End { get; set; }
}

static class SecureStorage
{
    public static readonly string DataDir = Path.Combine(
        Program.DryRun ? Path.GetTempPath() : Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "PcTimeGuard");

    public static string LogFile => Path.Combine(DataDir, "Logging.log");
    public static string ScheduleCacheFile => Path.Combine(DataDir, "schedule.cache.json");
    private static string LastSeenFile => Path.Combine(DataDir, "lastseen.txt");

    public static void Initialize()
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(DataDir);
            Logger.Log($"[DRY RUN] Using {DataDir}; on Windows it is restricted to SYSTEM and Administrators");
            return;
        }

        InitializeWindows();
    }

    [SupportedOSPlatform("windows")]
    private static void InitializeWindows()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        foreach (var sidType in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sidType, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        var directory = new DirectoryInfo(DataDir);
        if (!directory.Exists)
        {
            directory.Create(security);
            return;
        }

        // Standard users can create folders in ProgramData; one he pre-created would stay under his control.
        var owner = directory.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner == null ||
            !(owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)))
        {
            throw new InvalidOperationException($"{DataDir} is owned by {owner?.Value ?? "unknown"}");
        }

        directory.SetAccessControl(security);
    }

    public static string? ReadText(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    public static void WriteText(string path, string content)
    {
        string tempPath = path + ".tmp";
        File.WriteAllText(tempPath, content);
        File.Move(tempPath, path, overwrite: true);
    }

    public static DateTime? ReadLastSeenUtc()
    {
        string? text = ReadText(LastSeenFile);
        if (text != null &&
            DateTime.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime value))
        {
            return value.ToUniversalTime();
        }

        return null;
    }

    public static void WriteLastSeenUtc(DateTime valueUtc) =>
        WriteText(LastSeenFile, valueUtc.ToString("O", CultureInfo.InvariantCulture));
}

static class Logger
{
    public static void Log(string message)
    {
        try
        {
            string line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z  {message}{Environment.NewLine}";

            Console.WriteLine(line);

            // Only SecureStorage may create the folder, so it always gets locked-down permissions.
            if (Directory.Exists(SecureStorage.DataDir))
                File.AppendAllText(SecureStorage.LogFile, line);
        }
        catch
        {
            // Logging must never break
        }
    }
}
