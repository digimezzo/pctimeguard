using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

class Program
{
    private const string TimeApiUrl = "http://worldtimeapi.org/api/ip";

    private const string ScheduleUrl = "https://raw.githubusercontent.com/digimezzo/scheduling/main/schedule.json";

    static async Task Main()
    {
        try
        {
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10)
            };

            (DateTime nowUtc, Dictionary<string, TimeWindow> schedule) = await GetTimeAndScheduleWithRetry(client);

            TimeSpan now = nowUtc.TimeOfDay;
            string today = nowUtc.DayOfWeek.ToString();

            if (!schedule.TryGetValue(today, out TimeWindow? window))
            {
                ForceShutdown();
            }

            if (string.IsNullOrWhiteSpace(window!.Start) || string.IsNullOrWhiteSpace(window.End))
            {
                ForceShutdown();
            }

            TimeSpan start = TimeSpan.Parse(window.Start);
            TimeSpan end = TimeSpan.Parse(window.End);

            bool allowed = now >= start && now <= end;

            if (!allowed)
            {
                ForceShutdown();
            }
        }
        catch
        {
            ForceShutdown();
        }
    }

    static async Task<(DateTime, Dictionary<string, TimeWindow>)> GetTimeAndScheduleWithRetry(HttpClient client)
    {
        const int maxRetries = 3;
        const int delaySeconds = 30;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                string timeResponse = await client.GetStringAsync(TimeApiUrl);

                using var timeDoc = JsonDocument.Parse(timeResponse);
                string? datetimeStr = timeDoc.RootElement.GetProperty("datetime").GetString();

                if (string.IsNullOrWhiteSpace(datetimeStr))
                {
                    throw new Exception("Invalid time response");
                }

                DateTime nowUtc = DateTime.Parse(datetimeStr);

                string scheduleJson = await client.GetStringAsync(ScheduleUrl);

                var schedule = JsonSerializer.Deserialize<Dictionary<string, TimeWindow>>(scheduleJson) ?? throw new Exception("Invalid schedule");
                return (nowUtc, schedule);
            }
            catch
            {
                if (attempt == maxRetries)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
            }
        }

        // All retries failed
        ForceShutdown();
        throw new Exception("Unreachable");
    }

    static void ForceShutdown()
    {
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
