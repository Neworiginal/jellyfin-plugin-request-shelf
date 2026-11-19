using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RequestShelf;

internal sealed class ShelfError(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

internal sealed record Account(int Id, string? JellyfinUserId, long? PlexId, string? PlexUsername, string? DisplayName)
{
    public bool Matches(Guid owner) => Id > 0 && Guid.TryParse(JellyfinUserId, out var id) && owner == id;
}
internal sealed record ResultPage<T>(T[] Results);
internal sealed record Season(
    int SeasonNumber,
    int EpisodeCount,
    int Status,
    string? Name = null,
    string? Overview = null,
    string? AirDate = null,
    string? PosterPath = null);
internal sealed record RequestInfo(bool Is4k, int Status, Season[]? Seasons);
internal sealed record MediaInfo(int Status, Season[]? Seasons, RequestInfo[]? Requests, Download[]? DownloadStatus);
internal sealed record RequestMedia(int TmdbId, string? MediaType, int Status, Download[]? DownloadStatus);
internal sealed record SeerrRequest(int Id, int Status, bool Is4k, RequestMedia? Media, Season[]? Seasons, string? CreatedAt);

internal sealed record ExternalIds(int? TvdbId);
internal sealed record Title(int Id, string? MediaType, string? Name,
    string? Overview, string? ReleaseDate, string? FirstAirDate, string? PosterPath,
    bool Adult, MediaInfo? MediaInfo, Season[]? Seasons, string? Status = null, ExternalIds? ExternalIds = null)
{
    [System.Text.Json.Serialization.JsonPropertyName("title")]
    public string? MovieTitle { get; init; }
    public string DisplayName => MovieTitle ?? Name ?? "";
    public string? RawReleaseDate => string.IsNullOrWhiteSpace(ReleaseDate) ? FirstAirDate : ReleaseDate;
    public int? Year => int.TryParse(RawReleaseDate?.Split('-')[0], out var year) ? year : null;

    public DateOnly? ParsedReleaseDate =>
        DateOnly.TryParseExact(RawReleaseDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : null;

    public string? FormattedReleaseDate =>
        ParsedReleaseDate is { } d ? d.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)
        : !string.IsNullOrWhiteSpace(RawReleaseDate) ? RawReleaseDate : null;

    public bool IsUnreleased =>
        Status is "Rumored" or "Planned" or "In Production" or "Post Production"
        || (ParsedReleaseDate is { } d && d > DateOnly.FromDateTime(DateTime.UtcNow));
}

internal sealed record PublicSettings(bool? PartialRequestsEnabled);

public sealed class Seerr : IDisposable
{
    private readonly HttpClient _http = new(new SocketsHttpHandler {
        AllowAutoRedirect = false, UseCookies = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }) { Timeout = TimeSpan.FromSeconds(9), MaxResponseContentBufferSize = 4 * 1024 * 1024 };

    private async Task<T> Send<T>(Configuration config, string path, int? account, object? body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey) || !Uri.TryCreate(config.SeerrUrl.TrimEnd('/') + "/", UriKind.Absolute, out var root)
            || (root.Scheme != "http" && root.Scheme != "https") || root.UserInfo.Length + root.Fragment.Length + root.Query.Length != 0)
            throw new ShelfError(503, "Configure the Seerr address and API key in Request Shelf.");

        using var msg = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, new Uri(root, "api/v1/" + path));
        msg.Headers.Add("X-Api-Key", config.ApiKey);
        if (account.HasValue) msg.Headers.Add("X-Api-User", account.Value.ToString(CultureInfo.InvariantCulture));
        if (body is not null) msg.Content = JsonContent.Create(body);
        using var response = await _http.SendAsync(msg, ct);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var message = status switch {
                401 or 403 => "Seerr denied access. Check the key, account permissions and quota.",
                404 => "Seerr could not find that title or account.",
                409 => "Seerr reports a conflicting request. Reopen the card to see its status.",
                429 => "Seerr's request limit was reached. Try again later.",
                400 => "Seerr declined the request. Check the season, quota and existing requests.",
                _ => "Seerr could not complete the operation. Check the address and Seerr logs."
            };
            throw new ShelfError(status is >= 400 and < 500 ? status : 502, message);
        }
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct)
            ?? throw new ShelfError(502, "Seerr returned an empty response.");
    }

    private async IAsyncEnumerable<Account> Users(Configuration config, [EnumeratorCancellation] CancellationToken ct)
    {
        const int size = 50;
        for (int skip = 0; ; skip += size) {
            var page = await Send<ResultPage<Account>>(config, $"user?take={size}&skip={skip}&sort=created", null, null, ct);
            foreach (var user in page.Results) yield return user;
            if (page.Results.Length < size) yield break;
        }
    }

    internal async Task<List<PlexUserChoice>> PlexUsers(Configuration config, CancellationToken ct)
    {
        var users = new List<PlexUserChoice>();
        await foreach (var account in Users(config, ct)) {
            if (account.Id > 0 && account.PlexId is > 0)
                users.Add(new(account.Id, account.PlexId.Value, account.PlexUsername ?? account.DisplayName ?? $"User {account.Id}"));
        }
        return users.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static PlexAccountLink? Link(Configuration config, Guid owner) =>
        (config.PlexLinks ?? []).FirstOrDefault(x => x.JellyfinUserId == owner);

    private async Task VerifyAccount(Configuration config, Guid owner, int accountId, CancellationToken ct)
    {
        if (accountId <= 0) throw new ShelfError(403, "Choose a valid Seerr account in Request Shelf settings.");
        var me = await Send<Account>(config, "auth/me", accountId, null, ct);
        var link = Link(config, owner);
        var sameIdentity = link is null ? me.Matches(owner)
            : link.SeerrUserId == accountId && link.PlexId > 0 && me.PlexId == link.PlexId;
        if (me.Id != accountId || !sameIdentity)
            throw new ShelfError(403, "Seerr account identity changed. Review the pairing in Request Shelf settings.");
    }

    internal async Task<int> AccountFor(Configuration config, Guid owner, CancellationToken ct)
    {
        if (Link(config, owner) is { } link) {
            await VerifyAccount(config, owner, link.SeerrUserId, ct);
            return link.SeerrUserId;
        }

        // Earlier Seerr releases lack the direct lookup. Compare immutable Jellyfin IDs on both paths.
        try {
            var found = await Send<Account>(config, $"user/jellyfin/{owner:N}", null, null, ct);
            if (found.Matches(owner)) return found.Id;
        } catch (ShelfError ex) when (ex.Status == 404) { }
        await foreach (var account in Users(config, ct))
            if (account.Matches(owner)) return account.Id;
        throw new ShelfError(403, "Choose this viewer's Plex account in Request Shelf settings, or link their Jellyfin account in Seerr.");
    }

    internal async Task<Title[]> Search(Configuration c, int account, string term, CancellationToken ct) =>
        (await Send<ResultPage<Title>>(c, "search?page=1&query=" + Uri.EscapeDataString(term), account, null, ct)).Results;

    internal Task<Title> Details(Card card, CancellationToken ct) =>
        Details(card.Config, card.Account, card.Type, card.Title.Id, ct);

    internal Task<Title> Details(Configuration c, int account, string mediaType, int tmdbId, CancellationToken ct) =>
        Send<Title>(c, $"{mediaType}/{tmdbId}", account, null, ct);

    internal async Task<SeerrRequest[]> Requests(Configuration c, int account, CancellationToken ct) =>
        (await Send<ResultPage<SeerrRequest>>(c, "request?take=40&filter=all&sort=added", account, null, ct)).Results;

    internal async Task<int> Submit(Card card, CancellationToken ct)
    {
        await VerifyAccount(card.Config, card.Owner, card.Account, ct);
        if (card.Type == "tv" && card.Season > 0) {
            var settings = await Send<PublicSettings>(card.Config, "settings/public", card.Account, null, ct);
            if (settings.PartialRequestsEnabled != true)
                throw new ShelfError(409, "Individual season requests are disabled in Seerr. Search again with all seasons.");
        }
        object payload = card.Type == "movie" ? new { mediaType = "movie", mediaId = card.Title.Id, is4k = false }
            : new { mediaType = "tv", mediaId = card.Title.Id, is4k = false,
                seasons = card.Season == 0 ? (object)"all" : new[] { card.Season } };
        var receipt = await Send<JsonElement>(card.Config, "request", card.Account, payload, ct);
        if (!receipt.TryGetProperty("id", out var id) || !id.TryGetInt32(out int n) || n <= 0)
            throw new ShelfError(502, "Seerr returned no request number. Reopen the card to check before retrying.");
        return receipt.TryGetProperty("status", out var state) && state.TryGetInt32(out var value) ? value : 0;
    }

    internal static bool HasPoster(string? path) => path is not null && Regex.IsMatch(path, @"^/[A-Za-z0-9_-]{1,128}\.jpg$");

    internal async Task<byte[]> Poster(string path, CancellationToken ct)
    {
        if (!HasPoster(path)) throw new ShelfError(404, "No poster.");
        // A separate, header-free request to a fixed public image host.
        return await _http.GetByteArrayAsync(new Uri("https://image.tmdb.org/t/p/w342" + path), ct);
    }

    public void Dispose() => _http.Dispose();
}
