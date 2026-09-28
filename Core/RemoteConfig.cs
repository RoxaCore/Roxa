using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XrayClient.Core;

public sealed class UpdateInfo
{
    public string Latest { get; set; } = "";
    public string Notes { get; set; } = "";
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public bool Force { get; set; }
    public bool UpdateAvailable { get; set; }
    public long Size { get; set; }
}

public sealed class Announcement
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string Level { get; set; } = "info";
    public string Url { get; set; } = "";
}

public static class RemoteConfig
{
    public const string BaseUrl = "https://up.roxacore.com";
    private const string UserAgent = "Roxa/3.0";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static string AppVersion
    {
        get
        {
            var asm = typeof(RemoteConfig).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                var plus = info.IndexOf('+');
                if (plus > 0) info = info[..plus];
                info = info.Trim();
                if (info.Length > 0) return info;
            }
            return asm.GetName().Version?.ToString(3) ?? "0.0.0";
        }
    }

    private sealed class CheckDto
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("latest")] public string? Latest { get; set; }
        [JsonPropertyName("notes")] public string? Notes { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
        [JsonPropertyName("force")] public bool Force { get; set; }
        [JsonPropertyName("update_available")] public bool UpdateAvailable { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
    }

    private sealed class AnnouncementsDto
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("items")] public List<Announcement>? Items { get; set; }
    }

    private static HttpClient CreateClient(int? proxyPort)
    {
        var handler = new HttpClientHandler();
        if (proxyPort is > 0)
        {
            handler.Proxy = new System.Net.WebProxy($"http://127.0.0.1:{proxyPort.Value}");
            handler.UseProxy = true;
        }
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        return http;
    }

    private static async Task<string?> FetchCountryAsync(string url, string propName, CancellationToken ct)
    {
        try
        {
            using var http = CreateClient(null);
            http.Timeout = TimeSpan.FromSeconds(6);
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var code = doc.RootElement.TryGetProperty(propName, out var v) ? v.GetString() : null;
            code = (code ?? "").Trim().ToUpperInvariant();
            return code.Length == 2 && code.All(char.IsLetter) ? code : null;
        }
        catch
        {
            return null;
        }
    }

    public static async Task<string?> DetectCountryAsync(CancellationToken ct = default)
    {
        var code = await FetchCountryAsync("https://get.geojs.io/v1/ip/country.json", "country", ct);
        if (code != null) return code;
        return await FetchCountryAsync("https://ipwho.is/", "country_code", ct);
    }

    public static async Task<UpdateInfo?> CheckUpdateAsync(string lang, string country, string clientId, int? proxyPort, CancellationToken ct = default)
    {
        try
        {
            using var http = CreateClient(proxyPort);
            var url = $"{BaseUrl}/api/check.php?v={Uri.EscapeDataString(AppVersion)}&lang={Uri.EscapeDataString(lang)}";
            if (!string.IsNullOrWhiteSpace(country))
                url += $"&country={Uri.EscapeDataString(country)}";
            if (!string.IsNullOrWhiteSpace(clientId))
                url += $"&id={Uri.EscapeDataString(clientId)}";
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct);
            var dto = JsonSerializer.Deserialize<CheckDto>(json, JsonOptions);
            if (dto is not { Ok: true } || string.IsNullOrWhiteSpace(dto.Latest)) return null;
            return new UpdateInfo
            {
                Latest = dto.Latest.Trim(),
                Notes = dto.Notes ?? "",
                Url = dto.Url ?? "",
                Sha256 = dto.Sha256 ?? "",
                Force = dto.Force,
                UpdateAvailable = dto.UpdateAvailable,
                Size = dto.Size,
            };
        }
        catch
        {
            return null;
        }
    }

    public static async Task<List<Announcement>?> FetchAnnouncementsAsync(int since, string lang, string country, int? proxyPort, CancellationToken ct = default)
    {
        try
        {
            using var http = CreateClient(proxyPort);
            var url = $"{BaseUrl}/api/announcements.php?since={since}&lang={Uri.EscapeDataString(lang)}";
            if (!string.IsNullOrWhiteSpace(country))
                url += $"&country={Uri.EscapeDataString(country)}";
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct);
            var dto = JsonSerializer.Deserialize<AnnouncementsDto>(json, JsonOptions);
            if (dto is not { Ok: true }) return null;
            var result = new List<Announcement>();
            foreach (var item in dto.Items ?? new List<Announcement>())
            {
                result.Add(new Announcement
                {
                    Id = item.Id,
                    Title = item.Title ?? "",
                    Body = item.Body ?? "",
                    Level = string.IsNullOrWhiteSpace(item.Level) ? "info" : item.Level,
                    Url = item.Url ?? "",
                });
            }
            return result;
        }
        catch
        {
            return null;
        }
    }
}
