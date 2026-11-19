using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RequestShelf;

internal sealed record ArrRootFolder(string? Path);
internal sealed record ArrQualityProfile(int Id, string? Name);

public sealed class ArrClient : IDisposable
{
    private readonly HttpClient _http = new(new SocketsHttpHandler {
        AllowAutoRedirect = false, UseCookies = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }) { Timeout = TimeSpan.FromSeconds(12), MaxResponseContentBufferSize = 4 * 1024 * 1024 };

    private static Uri RequireRoot(string? raw, string service)
    {
        if (string.IsNullOrWhiteSpace(raw) || !Uri.TryCreate(raw.TrimEnd('/') + "/", UriKind.Absolute, out var root)
            || (root.Scheme != "http" && root.Scheme != "https") || root.UserInfo.Length + root.Fragment.Length + root.Query.Length != 0)
            throw new ShelfError(503, $"Configure the {service} address and API key in Request Shelf settings.");
        return root;
    }

    private async Task<T> Send<T>(Uri root, string apiKey, HttpMethod method, string path, object? body, string service, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ShelfError(503, $"Configure the {service} API key in Request Shelf settings.");
        using var req = new HttpRequestMessage(method, new Uri(root, "api/v3/" + path));
        req.Headers.Add("X-Api-Key", apiKey);
        if (body is not null) req.Content = JsonContent.Create(body);
        using var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) {
            var code = (int)res.StatusCode;
            var msg = code is 401 or 403
                ? $"{service} denied access. Check the API key."
                : $"{service} returned HTTP {code}. Check {service} logs.";
            throw new ShelfError(code is >= 400 and < 500 ? code : 502, msg);
        }
        return await res.Content.ReadFromJsonAsync<T>(cancellationToken: ct)
            ?? throw new ShelfError(502, $"{service} returned an empty response.");
    }

    private async Task<(string RootFolder, int QualityProfileId, string QualityProfileName)> Defaults(
        Uri root, string apiKey, string service, CancellationToken ct)
    {
        var foldersTask = Send<ArrRootFolder[]>(root, apiKey, HttpMethod.Get, "rootfolder", null, service, ct);
        var profilesTask = Send<ArrQualityProfile[]>(root, apiKey, HttpMethod.Get, "qualityprofile", null, service, ct);
        await Task.WhenAll(foldersTask, profilesTask);
        var folder = foldersTask.Result.FirstOrDefault(f => !string.IsNullOrWhiteSpace(f.Path))?.Path
            ?? throw new ShelfError(502, $"{service} has no root folder configured.");
        var profile = profilesTask.Result.FirstOrDefault(p => p.Id > 0)
            ?? throw new ShelfError(502, $"{service} has no quality profile configured.");
        return (folder, profile.Id, profile.Name ?? $"Profile #{profile.Id}");
    }

    internal async Task<string> Test(string? url, string? apiKey, string service, CancellationToken ct)
    {
        var root = RequireRoot(url, service);
        var (folder, _, profile) = await Defaults(root, apiKey ?? "", service, ct);
        return $"{service} OK ({folder} · {profile})";
    }

    internal async Task Submit(Card card, Title detail, CancellationToken ct)
    {
        if (card.Type == "movie") {
            await SubmitMovie(card.Config, detail, ct);
        } else {
            await SubmitSeries(card.Config, detail, card.Season, ct);
        }
    }

    private async Task SubmitMovie(Configuration config, Title detail, CancellationToken ct)
    {
        var root = RequireRoot(config.RadarrUrl, "Radarr");
        var key = config.RadarrApiKey;
        var existing = await Send<JsonElement[]>(root, key, HttpMethod.Get, $"movie?tmdbId={detail.Id}", null, "Radarr", ct);
        if (existing.Length > 0 && existing[0].TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out int existingId) && existingId > 0) {
            await Send<JsonElement>(root, key, HttpMethod.Post, "command",
                new { name = "MoviesSearch", movieIds = new[] { existingId } }, "Radarr", ct);
            return;
        }

        var (folder, profileId, _) = await Defaults(root, key, "Radarr", ct);
        var payload = new {
            title = detail.DisplayName,
            tmdbId = detail.Id,
            year = detail.Year ?? 0,
            qualityProfileId = profileId,
            rootFolderPath = folder,
            monitored = true,
            addOptions = new { searchForMovie = true }
        };
        await Send<JsonElement>(root, key, HttpMethod.Post, "movie", payload, "Radarr", ct);
    }

    private async Task SubmitSeries(Configuration config, Title detail, int season, CancellationToken ct)
    {
        var root = RequireRoot(config.SonarrUrl, "Sonarr");
        var key = config.SonarrApiKey;
        int tvdbId = detail.ExternalIds?.TvdbId ?? 0;

        JsonElement? lookedUp = null;
        var lookupTerm = tvdbId > 0 ? $"tvdb:{tvdbId}" : $"tmdb:{detail.Id}";
        var matches = await Send<JsonElement[]>(root, key, HttpMethod.Get, "series/lookup?term=" + Uri.EscapeDataString(lookupTerm), null, "Sonarr", ct);
        if (matches.Length > 0) {
            lookedUp = matches[0];
            if (tvdbId <= 0 && lookedUp.Value.TryGetProperty("tvdbId", out var tEl) && tEl.TryGetInt32(out int foundTvdb))
                tvdbId = foundTvdb;
        }
        if (tvdbId <= 0)
            throw new ShelfError(404, "Sonarr could not find a TVDB match for this series.");

        var existing = await Send<JsonNode[]>(root, key, HttpMethod.Get, $"series?tvdbId={tvdbId}", null, "Sonarr", ct);
        if (existing.Length > 0 && existing[0] is JsonObject seriesObj && seriesObj["id"]?.GetValue<int>() is > 0 and int seriesId) {
            if (seriesObj["seasons"] is JsonArray seasonsArr) {
                foreach (var node in seasonsArr) {
                    if (node is JsonObject sObj && sObj["seasonNumber"]?.GetValue<int>() is int sn && sn > 0) {
                        if (season == 0 || sn == season)
                            sObj["monitored"] = true;
                    }
                }
                seriesObj["monitored"] = true;
                await Send<JsonElement>(root, key, HttpMethod.Put, $"series/{seriesId}", seriesObj, "Sonarr", ct);
            }
            object cmd = season > 0
                ? new { name = "SeasonSearch", seriesId, seasonNumber = season }
                : new { name = "SeriesSearch", seriesId };
            await Send<JsonElement>(root, key, HttpMethod.Post, "command", cmd, "Sonarr", ct);
            return;
        }

        var (folder, profileId, _) = await Defaults(root, key, "Sonarr", ct);
        var seasons = (detail.Seasons ?? [])
            .Where(s => s.SeasonNumber >= 0)
            .Select(s => new {
                seasonNumber = s.SeasonNumber,
                monitored = s.SeasonNumber > 0 && (season == 0 || s.SeasonNumber == season)
            })
            .ToArray();

        var title = lookedUp is { } lu && lu.TryGetProperty("title", out var titleEl) && !string.IsNullOrWhiteSpace(titleEl.GetString())
            ? titleEl.GetString()! : detail.DisplayName;

        var payload = new {
            title,
            tvdbId,
            qualityProfileId = profileId,
            rootFolderPath = folder,
            monitored = true,
            seasonFolder = true,
            seasons,
            addOptions = new {
                searchForMissingEpisodes = true,
                searchForCutoffUnmetEpisodes = false
            }
        };
        await Send<JsonElement>(root, key, HttpMethod.Post, "series", payload, "Sonarr", ct);
    }

    public void Dispose() => _http.Dispose();
}
