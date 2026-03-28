using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

class Program
{
    private const string TimeApiUrl =
        "http://worldtimeapi.org/api/ip";

    private const string ScheduleUrl =
        "https://raw.githubusercontent.com/digimezzo/scheduling/main/schedule.json";

    static async Task Main()
    {
        Logger.Log("Program started");

        try
        {
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10)
            };

            (DateTime nowUtc, Dictionary<string, List<TimeWindow>> schedule) = await GetTimeAndScheduleWithRetry(client);

            TimeSpan now = nowUtc.TimeOfDay;
            string today = nowUtc.DayOfWeek.ToString();

            Logger.Log($"Time OK: {nowUtc:O} (Today={today})");

            if (!schedule.TryGetValue(today, out List<TimeWindow>? windows) || windows == null || windows.Count == 0)
            {
                string reason = $"No schedule entry for {today}";
                Logger.Log(reason);
                DelayedShutdown(reason);
                return;
            }

            bool allowed = false;
            foreach (var window in windows)
            {
                if (string.IsNullOrWhiteSpace(window.Start) || string.IsNullOrWhiteSpace(window.End))
                    continue;

                TimeSpan start = TimeSpan.Parse(window.Start);
                TimeSpan end = TimeSpan.Parse(window.End);

                Logger.Log($"Allowed window: {start} --> {end}, now={now}");

                if (now >= start && now <= end)
                {
                    allowed = true;
                    break;
                }
            }

            if (!allowed)
            {
                string reason = "Outside allowed window";
                Logger.Log(reason);
                ShortDelayedShutdown(reason);
                return;
            }

            Logger.Log("Within allowed window");
        }
        catch (Exception ex)
        {
            string reason = $"Unhandled exception: {ex}";
            Logger.Log(reason);
            DelayedShutdown(reason);
        }
    }

    static async Task<(DateTime, Dictionary<string, List<TimeWindow>>)> GetTimeAndScheduleWithRetry(HttpClient client)
    {
        const int maxRetries = 3;
        const int delaySeconds = 30;

        DateTime? resolvedTime = null;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                if (resolvedTime == null)
                {
                    try
                    {
                        Logger.Log($"Attempt {attempt}: fetching internet time");

                        string timeResponse = await client.GetStringAsync(TimeApiUrl);

                        using var timeDoc = JsonDocument.Parse(timeResponse);
                        string? datetimeStr =
                            timeDoc.RootElement.GetProperty("datetime").GetString();

                        if (string.IsNullOrWhiteSpace(datetimeStr))
                            throw new Exception("Invalid time response");

                        resolvedTime = DateTime.Parse(datetimeStr);
                        Logger.Log($"Internet time OK: {resolvedTime:O}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Internet time failed: {ex.Message}");
                        Logger.Log("Falling back to local PC time");

                        resolvedTime = DateTime.Now;
                    }
                }

                Logger.Log("Fetching schedule");

                string scheduleJson =
                    await client.GetStringAsync(ScheduleUrl);

                var schedule = ParseScheduleJson(scheduleJson);

                Logger.Log($"Schedule OK ({schedule.Count} days)");

                return (resolvedTime.Value, schedule);
            }
            catch (Exception ex)
            {
                Logger.Log($"Attempt {attempt} failed: {ex.Message}");

                if (attempt == maxRetries)
                    break;

                Logger.Log($"Retrying in {delaySeconds} seconds");
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
            }
        }

        string reason = "Unable to fetch schedule after retries";
        Logger.Log(reason);
        DelayedShutdown(reason);
        throw new Exception("Unreachable");
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


    // static void ForceShutdown()
    // {
    //     Logger.Log("FORCE SHUTDOWN triggered");

    //     Process.Start(new ProcessStartInfo
    //     {
    //         FileName = "shutdown",
    //         Arguments = "/s /f /t 0",
    //         CreateNoWindow = true,
    //         UseShellExecute = false
    //     });

    //     Environment.Exit(0);
    // }

    static void DelayedShutdown(string reason)
    {
        Logger.Log("DELAYED SHUTDOWN: " + reason);

        Process.Start(new ProcessStartInfo
        {
            FileName = "shutdown",
            Arguments = $"/s /f /t 300 /c \"PC will shut down in 5 minutes. Reason: {reason}\"",
            CreateNoWindow = true,
            UseShellExecute = false
        });

        Environment.Exit(0);
    }

    static void ShortDelayedShutdown(string reason)
    {
        Logger.Log("DELAYED SHUTDOWN: " + reason);

        Process.Start(new ProcessStartInfo
        {
            FileName = "shutdown",
            Arguments = $"/s /f /t 30 /c \"PC will shut down in 30 seconds. Reason: {reason}\"",
            CreateNoWindow = true,
            UseShellExecute = false
        });

        Environment.Exit(0);
    }
}

class TimeWindow
{
    [JsonPropertyName("start")]
    public required string Start { get; set; }

    [JsonPropertyName("end")]
    public required string End { get; set; }
}

static class Logger
{
    private static readonly string LogDir = @"C:\Temp";
    private static readonly string LogFile = Path.Combine(LogDir, "Logging.log");

    public static void Log(string message)
    {
        try
        {
            if (!Directory.Exists(LogDir))
            {
                Directory.CreateDirectory(LogDir);
            }

            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}";

            Console.WriteLine(line);
            File.AppendAllText(LogFile, line);
        }
        catch
        {
            // Logging must never break
        }
    }
}
