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

            (DateTime nowUtc, Dictionary<string, TimeWindow> schedule) = await GetTimeAndScheduleWithRetry(client);

            TimeSpan now = nowUtc.TimeOfDay;
            string today = nowUtc.DayOfWeek.ToString();

            Logger.Log($"Time OK: {nowUtc:O} (Today={today})");

            if (!schedule.TryGetValue(today, out TimeWindow? window))
            {
                Logger.Log($"No schedule entry for {today}");
                ForceShutdown();
            }

            if (string.IsNullOrWhiteSpace(window!.Start) || string.IsNullOrWhiteSpace(window.End))
            {
                Logger.Log("Schedule start or end is empty");
                ForceShutdown();
            }

            TimeSpan start = TimeSpan.Parse(window.Start);
            TimeSpan end = TimeSpan.Parse(window.End);

            Logger.Log($"Allowed window: {start} --> {end}, now={now}");

            bool allowed = now >= start && now <= end;

            if (!allowed)
            {
                Logger.Log("Outside allowed window --> shutdown");
                ForceShutdown();
            }

            Logger.Log("Within allowed window");
        }
        catch (Exception ex)
        {
            Logger.Log($"Unhandled exception: {ex}");
            ForceShutdown();
        }
    }

    static async Task<(DateTime, Dictionary<string, TimeWindow>)>
        GetTimeAndScheduleWithRetry(HttpClient client)
    {
        const int maxRetries = 3;
        const int delaySeconds = 30;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                Logger.Log($"Attempt {attempt}: fetching internet time");

                string timeResponse = await client.GetStringAsync(TimeApiUrl);

                using var timeDoc = JsonDocument.Parse(timeResponse);
                string? datetimeStr = timeDoc.RootElement.GetProperty("datetime").GetString();

                if (string.IsNullOrWhiteSpace(datetimeStr))
                {
                    throw new Exception("Invalid time response");
                }


                DateTime nowUtc = DateTime.Parse(datetimeStr);

                Logger.Log($"Internet time OK: {nowUtc:O}");

                Logger.Log("Fetching schedule");

                string scheduleJson = await client.GetStringAsync(ScheduleUrl);

                var schedule = JsonSerializer.Deserialize<Dictionary<string, TimeWindow>>(scheduleJson) ?? throw new Exception("Invalid schedule");

                Logger.Log($"Schedule OK ({schedule.Count} days)");

                return (nowUtc, schedule);
            }
            catch (Exception ex)
            {
                Logger.Log($"Attempt {attempt} failed: {ex.Message}");

                if (attempt == maxRetries)
                {
                    break;
                }

                Logger.Log($"Retrying in {delaySeconds} seconds");
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
            }
        }

        Logger.Log("All retries exhausted → shutdown");
        ForceShutdown();
        throw new Exception("Unreachable");
    }

    static void ForceShutdown()
    {
        Logger.Log("FORCE SHUTDOWN triggered");

        Process.Start(new ProcessStartInfo
        {
            FileName = "shutdown",
            Arguments = "/s /f /t 0",
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
            // Logging must never break enforcement
        }
    }
}
