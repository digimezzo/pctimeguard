using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

class Program
{
    static async Task Main()
    {
        try
        {
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10)
            };

            // 1. Get current internet time
            string timeResponse = await client.GetStringAsync("http://worldtimeapi.org/api/ip");
            using var timeDoc = JsonDocument.Parse(timeResponse);
            string datetimeStr = timeDoc.RootElement.GetProperty("datetime").GetString();
            DateTime nowUtc = DateTime.Parse(datetimeStr);
            TimeSpan now = nowUtc.TimeOfDay;
            string today = nowUtc.DayOfWeek.ToString();

            // 2. Download schedule
            string scheduleUrl =
                "https://raw.githubusercontent.com/digimezzo/scheduling/main/schedule.json";

            string scheduleJson = await client.GetStringAsync(scheduleUrl);

            var schedule = JsonSerializer.Deserialize<
                Dictionary<string, TimeWindow>>(scheduleJson);

            if (schedule == null || !schedule.ContainsKey(today))
                ForceShutdown();

            TimeSpan start = TimeSpan.Parse(schedule[today].Start);
            TimeSpan end = TimeSpan.Parse(schedule[today].End);

            bool allowed = now >= start && now <= end;

            if (!allowed)
                ForceShutdown();
        }
        catch
        {
            // ANY failure = shutdown
            ForceShutdown();
        }
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
    public string Start { get; set; }

    [JsonPropertyName("end")]
    public string End { get; set; }
}
