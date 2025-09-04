using ChizuChan.Extensions;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ChizuChan.Providers
{
    public sealed class WeatherStatusProvider : IStatusProvider
    {
        private readonly IHttpClientFactory _factory;
        private readonly ILogger<WeatherStatusProvider> _log;
        private readonly double _lat = 43.6532, _lon = -79.3832; // Toronto

        public WeatherStatusProvider(IHttpClientFactory factory, ILogger<WeatherStatusProvider> log)
        {
            _factory = factory;
            _log = log;
        }

        public async Task<DynamicStatus?> GetAsync(CancellationToken ct = default)
        {
            try
            {
                var http = _factory.CreateClient("weather");
                string url =
                    $"https://api.open-meteo.com/v1/forecast?latitude={_lat}&longitude={_lon}&current=temperature_2m,weather_code&daily=temperature_2m_max,temperature_2m_min&timezone=America%2FToronto";

                using var resp = await http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    _log.LogWarning("Weather: HTTP {Status}", (int)resp.StatusCode);
                    return null;
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                double current = doc.RootElement.GetProperty("current").GetProperty("temperature_2m").GetDouble();
                int code = doc.RootElement.GetProperty("current").GetProperty("weather_code").GetInt32();
                var daily = doc.RootElement.GetProperty("daily");
                double tMax = daily.GetProperty("temperature_2m_max")[0].GetDouble();
                double tMin = daily.GetProperty("temperature_2m_min")[0].GetDouble();

                string condition = code switch
                {
                    0 => "Clear",
                    1 or 2 => "Partly cloudy",
                    3 => "Cloudy",
                    45 or 48 => "Fog",
                    51 or 53 or 55 or 56 or 57 => "Drizzle",
                    61 or 63 or 65 => "Rain",
                    66 or 67 => "Freezing rain",
                    71 or 73 or 75 or 77 => "Snow",
                    80 or 81 or 82 => "Showers",
                    95 or 96 or 99 => "Thunderstorm",
                    _ => "—"
                };
                string text = $"Toronto: {condition}, {current:0}°C (H {tMax:0}° / L {tMin:0}°)";
                _log.LogInformation("Weather: {Text} (code {Code})", text, code);

                return new DynamicStatus(text, PresenceKind.Watching);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Weather provider failed");
                return null;
            }
        }
    }
}
