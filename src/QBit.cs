using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace RequestShelf;

internal sealed record QBitTorrent(
    [property: JsonPropertyName("hash")] string? Hash,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("progress")] double Progress,
    [property: JsonPropertyName("dlspeed")] long DlSpeed,
    [property: JsonPropertyName("eta")] long Eta,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("amount_left")] long AmountLeft,
    [property: JsonPropertyName("num_seeds")] int NumSeeds,
    [property: JsonPropertyName("num_leechs")] int NumLeechs,
    [property: JsonPropertyName("added_on")] long AddedOn)
{
    public string NormalizedState => State?.Trim() ?? "";

    public bool IsError => NormalizedState is "error" or "missingFiles";

    public bool IsCompleted => Progress >= 0.9999 ||
        NormalizedState is "uploading" or "stalledUP" or "pausedUP" or "stoppedUP" or "queuedUP" or "forcedUP" or "checkingUP";

    public bool IsActiveDownload => !IsCompleted && (
        NormalizedState is "downloading" or "forcedDL" or "stalledDL" or "metaDL" or "forcedMetaDL"
            or "queuedDL" or "pausedDL" or "stoppedDL" or "checkingDL" or "checkingResumeData" or "allocating"
            or "error" or "missingFiles"
        || AmountLeft > 0);

    public string StateLabel => NormalizedState switch
    {
        "downloading" or "forcedDL" => "Downloading",
        "stalledDL" => NumSeeds == 0 ? "Stalled (0 seeders)" : "Stalled",
        "metaDL" or "forcedMetaDL" => "Fetching metadata",
        "queuedDL" => "Queued",
        "pausedDL" or "stoppedDL" => "Paused",
        "checkingDL" or "checkingUP" or "checkingResumeData" => "Checking",
        "allocating" => "Allocating",
        "error" or "missingFiles" => "Download error",
        _ when IsCompleted => "Downloaded · Importing",
        _ => "Downloading"
    };

    public static string? FormatSpeed(long bytesPerSec) => bytesPerSec switch
    {
        <= 0 => null,
        >= 1024L * 1024 * 1024 => $"{bytesPerSec / (1024.0 * 1024 * 1024):0.##} GB/s",
        >= 1024L * 1024 => $"{bytesPerSec / (1024.0 * 1024):0.#} MB/s",
        >= 1024L => $"{bytesPerSec / 1024.0:0} KB/s",
        _ => $"{bytesPerSec} B/s"
    };

    public static string? FormatEta(long seconds)
    {
        if (seconds <= 0 || seconds >= 8_640_000) return null;
        if (seconds < 60) return "<1m";
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d {ts.Hours}h";
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        return $"{ts.Minutes}m";
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => "0 B",
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
        >= 1024L * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        >= 1024L => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B"
    };
}

public sealed class QBit : IDisposable
{
    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _cachedKey = "";
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;
    private QBitTorrent[] _cached = [];

    public QBit()
    {
        _http = new HttpClient(new SocketsHttpHandler {
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = _cookies,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        }) { Timeout = TimeSpan.FromSeconds(5), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
    }

    internal static bool IsConfigured(Configuration config) =>
        TryRoot(config, out _);

    private static bool TryRoot(Configuration config, out Uri root)
    {
        root = null!;
        if (string.IsNullOrWhiteSpace(config.QBitUrl)) return false;
        if (!Uri.TryCreate(config.QBitUrl.TrimEnd('/') + "/", UriKind.Absolute, out var uri)) return false;
        if ((uri.Scheme != "http" && uri.Scheme != "https") || uri.UserInfo.Length + uri.Fragment.Length + uri.Query.Length != 0)
            return false;
        root = uri;
        return true;
    }

    internal static HashSet<string> AllowedCategories(Configuration config) =>
        (config.QBitCategories ?? "")
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private async Task Login(Uri root, Configuration config, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.QBitUsername) && string.IsNullOrWhiteSpace(config.QBitPassword))
            return;
        using var content = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("username", config.QBitUsername ?? ""),
            new KeyValuePair<string, string>("password", config.QBitPassword ?? "")
        ]);
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(root, "api/v2/auth/login")) { Content = content };
        req.Headers.Add("Referer", root.ToString());
        using var res = await _http.SendAsync(req, ct);
        var body = (await res.Content.ReadAsStringAsync(ct)).Trim();
        if (!res.IsSuccessStatusCode || !string.Equals(body, "Ok.", StringComparison.OrdinalIgnoreCase))
            throw new ShelfError(403, "qBittorrent login failed. Check the username and password.");
    }

    private async Task<QBitTorrent[]> FetchRaw(Uri root, Configuration config, CancellationToken ct)
    {
        var endpoint = new Uri(root, "api/v2/torrents/info");
        using var res = await _http.GetAsync(endpoint, ct);
        if (res.StatusCode == HttpStatusCode.Forbidden) {
            await Login(root, config, ct);
            using var retry = await _http.GetAsync(endpoint, ct);
            if (!retry.IsSuccessStatusCode)
                throw new ShelfError((int)retry.StatusCode, "qBittorrent denied access after login.");
            return await retry.Content.ReadFromJsonAsync<QBitTorrent[]>(cancellationToken: ct) ?? [];
        }
        if (!res.IsSuccessStatusCode)
            throw new ShelfError(502, $"qBittorrent returned HTTP {(int)res.StatusCode}.");
        return await res.Content.ReadFromJsonAsync<QBitTorrent[]>(cancellationToken: ct) ?? [];
    }

    internal async Task<QBitTorrent[]> GetTorrents(Configuration config, CancellationToken ct, bool bypassCache = false)
    {
        if (!TryRoot(config, out var root)) return [];
        var cacheKey = $"{root}|{config.QBitUsername}|{config.QBitPassword}";
        if (!bypassCache && cacheKey == _cachedKey && DateTimeOffset.UtcNow - _cachedAt < TimeSpan.FromSeconds(2))
            return _cached;

        await _gate.WaitAsync(ct);
        try {
            if (!bypassCache && cacheKey == _cachedKey && DateTimeOffset.UtcNow - _cachedAt < TimeSpan.FromSeconds(2))
                return _cached;
            var torrents = await FetchRaw(root, config, ct);
            _cached = torrents;
            _cachedKey = cacheKey;
            _cachedAt = DateTimeOffset.UtcNow;
            return torrents;
        } finally { _gate.Release(); }
    }

    internal async Task<IReadOnlyDictionary<string, QBitTorrent>> GetTorrentMap(Configuration config, CancellationToken ct)
    {
        if (!IsConfigured(config)) return new Dictionary<string, QBitTorrent>(StringComparer.OrdinalIgnoreCase);
        try {
            var list = await GetTorrents(config, ct);
            var map = new Dictionary<string, QBitTorrent>(list.Length, StringComparer.OrdinalIgnoreCase);
            foreach (var t in list) {
                if (!string.IsNullOrWhiteSpace(t.Hash))
                    map[t.Hash] = t;
            }
            return map;
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch {
            // If qBittorrent is temporarily unreachable, fall back cleanly to Seerr's status.
            return new Dictionary<string, QBitTorrent>(StringComparer.OrdinalIgnoreCase);
        }
    }

    internal async Task<(int Total, int Active)> TestConnection(Configuration config, CancellationToken ct)
    {
        if (!TryRoot(config, out _))
            throw new ShelfError(400, "Enter a valid HTTP or HTTPS qBittorrent address first.");
        var torrents = await GetTorrents(config, ct, bypassCache: true);
        var categories = AllowedCategories(config);
        var filtered = categories.Count == 0 ? torrents : torrents.Where(t => categories.Contains(t.Category ?? "")).ToArray();
        return (filtered.Length, filtered.Count(t => t.IsActiveDownload));
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
