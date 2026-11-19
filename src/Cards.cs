using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Library;
using MediaBrowser.Model.Search;
using Microsoft.Extensions.Caching.Memory;

namespace RequestShelf;

internal sealed record SearchSpec(string Term, int Season, bool TvOnly)
{
    public static SearchSpec? Parse(string? input)
    {
        string text = input?.TrimStart() ?? "";
        if (!text.StartsWith("req ", StringComparison.OrdinalIgnoreCase)) return null;
        return FromTerm(text[4..].Trim());
    }

    public static SearchSpec FromTerm(string term)
    {
        var suffix = Regex.Match(term, @"\s+(?:s([1-9][0-9]{0,3})|all seasons)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!suffix.Success) return new(term, 0, false);
        int season = suffix.Groups[1].Success ? int.Parse(suffix.Groups[1].Value) : 0;
        return new(term[..suffix.Index].TrimEnd(), season, true);
    }

    public static bool IsDownloadsQuery(string? input)
    {
        var text = input?.Trim() ?? "";
        return text.Equals("downloads", StringComparison.OrdinalIgnoreCase)
            || text.Equals("req downloads", StringComparison.OrdinalIgnoreCase)
            || text.Equals("req queue", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record State(
    string Label,
    bool Covered = false,
    bool Allowed = true,
    string? Detail = null,
    DownloadProgress? Progress = null);

internal sealed record DownloadEntry(
    Card Card,
    State State,
    int SortGroup,
    double SortProgress,
    string? ReleaseDate);

internal sealed record Card(
    Guid Id,
    Guid Owner,
    Configuration Config,
    int Account,
    Title Title,
    int Season,
    QBitTorrent? StandaloneTorrent = null,
    Guid? ParentSeriesId = null,
    string? SeasonPosterPath = null,
    string? SeasonOverview = null)
{
    public bool IsSeasonCard => ParentSeriesId.HasValue && Season > 0;
    public string Type => Title.MediaType ?? "movie";
    public BaseItemKind Kind => Type == "movie" ? BaseItemKind.Movie : IsSeasonCard ? BaseItemKind.Season : BaseItemKind.Series;
    public string Scope => Type == "movie" ? "Movie" : Season == 0 ? "All seasons" : $"Season {Season}";
    public string ArtworkTag => Id.ToString("N");
    public string? EffectivePosterPath => Seerr.HasPoster(SeasonPosterPath) ? SeasonPosterPath : Title.PosterPath;
    public bool WithPoster => Seerr.HasPoster(EffectivePosterPath);

    public State ReadState(Title data, bool detail, IReadOnlyDictionary<string, QBitTorrent>? qbit = null)
    {
        if (StandaloneTorrent is not null) {
            var live = !string.IsNullOrWhiteSpace(StandaloneTorrent.Hash) && qbit is not null && qbit.TryGetValue(StandaloneTorrent.Hash, out var current)
                ? current : StandaloneTorrent;
            var p = Download.DescribeTorrents([live]);
            return new(p.ShortLabel, true, true, p.Detail, p);
        }

        var state = Availability(data, detail);
        var info = data.MediaInfo;
        if (!state.Allowed || info is null || info.Status == 5) {
            if (!state.Covered && state.Allowed && data.IsUnreleased) {
                var rel = data.FormattedReleaseDate is { } d ? $"Not released yet (releases {d})." : "Not released yet.";
                return new("Pre-request", false, true, rel);
            }
            return state;
        }

        var queue = (info.DownloadStatus ?? []).Where(x => Type == "movie" || Season == 0 || x.Episode?.SeasonNumber == Season);
        var progress = Download.Describe(queue, qbit);
        if (progress is not null) {
            var detailText = Type == "tv" ? $"{progress.Detail} ({Scope})" : progress.Detail;
            return new(progress.ShortLabel, state.Covered || Type == "movie" || Season > 0, true, detailText, progress);
        }

        if (state.Label == "Processing") {
            if (data.IsUnreleased) {
                var rel = data.FormattedReleaseDate is { } d
                    ? $"Not released yet (releases {d}). Requested and monitored — will download automatically once released."
                    : "Not released yet. Requested and monitored — will download automatically once released.";
                var shortLabel = data.FormattedReleaseDate is { } rd ? $"Unreleased ({rd})" : "Unreleased";
                return new(shortLabel, true, true, rel);
            }
            return new("Searching", true, true, "Requested and approved. Waiting for Radarr/Sonarr to grab a release.");
        }

        if (!state.Covered && state.Allowed && data.IsUnreleased) {
            var rel = data.FormattedReleaseDate is { } d ? $"Not released yet (releases {d})." : "Not released yet.";
            return new("Pre-request", false, true, rel);
        }

        return state;
    }

    private State Availability(Title data, bool detail)
    {
        var info = data.MediaInfo;
        if (Type == "movie") return MediaState(info?.Status ?? 0);
        if (detail && !(data.Seasons ?? []).Any(s => s.EpisodeCount > 0 && s.SeasonNumber > 0 && (Season == 0 || s.SeasonNumber == Season)))
            return new("No matching season with episodes", false, false);

        if (info?.Status == 5) return new("Available according to Seerr", true);

        State SeasonState(int number) {
            var s = info?.Seasons?.FirstOrDefault(x => x.SeasonNumber == number);
            var state = MediaState(s?.Status ?? 0);
            if (state.Covered) return state;
            var r = info?.Requests?.FirstOrDefault(x => !x.Is4k && x.Status is 1 or 2
                && (x.Seasons ?? []).Any(y => y.SeasonNumber == number));
            return r is null ? state : MediaState(r.Status == 1 ? 2 : 3);
        }
        if (Season > 0) return SeasonState(Season);
        if (detail && data.Seasons!.Where(s => s.SeasonNumber > 0 && s.EpisodeCount > 0).All(s => SeasonState(s.SeasonNumber).Covered))
            return new("All seasons available or already requested", true);
        return new("Request");
    }

    private static State MediaState(int status) => status switch {
        2 => new("Pending approval", true),
        3 => new("Processing", true),
        4 => new("Partially available", true),
        5 => new("Available according to Seerr", true),
        _ => new("Request")
    };

    public UserItemDataDto UserData(State state) => new() { ItemId = Id, Key = Id.ToString("N"), IsFavorite = state.Covered };
    private string Caption(State state) => IsSeasonCard
        ? $"{state.Label} · Season {Season}"
        : $"{state.Label} · {Title.DisplayName}" + (StandaloneTorrent is null && Type == "tv" && Season > 0 ? $" · {Scope}" : "");

    public BaseItemDto Item(string server, State state, bool roku, string? overview = null)
    {
        var instruction = state.Covered || !state.Allowed ? state.Label + "."
            : Type == "tv" && !IsSeasonCard && Season == 0 && !roku
                ? "Select a season below to request individual seasons, or press Favorite (heart) here to request all seasons through Seerr."
                : $"Press Favorite (heart) to request {(Type == "movie" ? "this movie" : Scope.ToLowerInvariant())} through Seerr.";
        var header = state.Detail is null ? instruction : state.Covered ? state.Detail : state.Detail + " " + instruction;
        var body = overview ?? SeasonOverview ?? Title.Overview ?? "";
        return new() {
            Id = Id, ServerId = server, Name = Caption(state), OriginalTitle = Title.DisplayName,
            SeriesId = ParentSeriesId, SeriesName = IsSeasonCard ? Title.DisplayName : null,
            IndexNumber = IsSeasonCard ? Season : null,
            Type = roku || StandaloneTorrent is not null ? BaseItemKind.Movie : Kind,
            MediaType = roku || StandaloneTorrent is not null || Type == "movie" ? MediaType.Video : MediaType.Unknown,
            LocationType = LocationType.Virtual, PlayAccess = PlayAccess.None,
            IsFolder = !roku && StandaloneTorrent is null && Type == "tv",
            CanDelete = false, CanDownload = false, ProductionYear = Title.Year,
            Overview = string.IsNullOrWhiteSpace(body) ? header : header + "\n\n" + body,
            UserData = UserData(state),
            // Roku's movie detail screen indexes source zero even for a virtual item.
            MediaSources = roku ? [new() { Id = ArtworkTag, Name = "Request card", Container = "",
                VideoType = VideoType.VideoFile, SupportsDirectPlay = false, SupportsDirectStream = false,
                SupportsTranscoding = false, SupportsProbing = false }] : [],
            MediaStreams = [], Genres = [], People = [],
            Studios = [], Tags = [], ProviderIds = [], BackdropImageTags = [], Taglines = [],
            ImageTags = WithPoster ? new() { [ImageType.Primary] = ArtworkTag } : []
        };
    }

    // Older hint consumers still read ItemId.
#pragma warning disable CS0618
    public SearchHint Hint(State state) => new() {
        Id = Id, ItemId = Id, Name = Caption(state), Type = Kind, IsFolder = Type == "tv",
        ProductionYear = Title.Year, MediaType = Type == "movie" ? MediaType.Video : MediaType.Unknown,
        PrimaryImageTag = WithPoster ? ArtworkTag : null
    };
#pragma warning restore CS0618
}

public sealed class CardStore(Seerr seerr, QBit qbit, ArrClient arr) : IDisposable
{
    internal static readonly Guid DownloadsViewId = new("7f3a1c2e-9b4d-4e8a-b1c5-2d6f8a9c0e1b");

    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 8192 });
    private readonly byte[] _salt = RandomNumberGenerator.GetBytes(32);
    // Stripes bound memory and serialize concurrent search rows and repeated presses for the same title.
    private readonly SemaphoreSlim[] _gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private SemaphoreSlim Gate(object key) => _gates[(uint)key.GetHashCode() % _gates.Length];

    private void Save<T>(object key, T value, TimeSpan ttl) => _cache.Set(key, value,
        new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = ttl });
    internal Card? Find(Guid id) => _cache.Get<Card>(id);

    private Guid DeriveId(string seed) => new(HMACSHA256.HashData(_salt, Encoding.UTF8.GetBytes(seed)).AsSpan(0, 16));

    internal static BaseItemDto DownloadsFolder(string serverId) => new() {
        Id = DownloadsViewId,
        ServerId = serverId,
        Name = "Downloads & Requests",
        Type = BaseItemKind.CollectionFolder,
        IsFolder = true,
        LocationType = LocationType.Virtual,
        PlayAccess = PlayAccess.None,
        CanDelete = false,
        CanDownload = false,
        Overview = "Active downloads and pending requests from Seerr and qBittorrent.",
        MediaSources = [], MediaStreams = [], Genres = [], People = [],
        Studios = [], Tags = [], ProviderIds = [], BackdropImageTags = [], Taglines = [], ImageTags = []
    };

    private async Task<int> ResolveAccount(Configuration c, Guid owner, CancellationToken ct)
    {
        var accountKey = (c, owner);
        if (!_cache.TryGetValue(accountKey, out int account)) {
            account = await seerr.AccountFor(c, owner, ct);
            Save(accountKey, account, TimeSpan.FromMinutes(2));
        }
        return account;
    }

    internal async Task<Card[]> Search(Configuration c, Guid owner, SearchSpec spec, CancellationToken ct)
    {
        var key = (c, owner, spec);
        var gate = Gate(key);
        await gate.WaitAsync(ct);
        try {
            if (_cache.TryGetValue(key, out Card[]? cached) && cached is not null && cached.All(x => Find(x.Id) is not null)) return cached;
            var account = await ResolveAccount(c, owner, ct);
            var titles = await seerr.Search(c, account, spec.Term, ct);
            var cards = new List<Card>();
            foreach (var title in titles.Where(t => t.MediaType is "movie" or "tv" && t.Id > 0 && !t.Adult && t.DisplayName.Length > 0)) {
                if (spec.TvOnly && title.MediaType != "tv") continue;
                var id = DeriveId($"{owner:N}/{account}/{title.MediaType}/{title.Id}/{spec.Season}");
                var card = new Card(id, owner, c, account, title, spec.Season);
                Save(id, card, TimeSpan.FromHours(1));
                cards.Add(card);
            }
            var result = cards.ToArray();
            Save(key, result, TimeSpan.FromSeconds(12));
            return result;
        } finally { gate.Release(); }
    }

    internal async Task<(Card Card, State State)[]> GetSeasons(Card seriesCard, CancellationToken ct)
    {
        var detailTask = seerr.Details(seriesCard, ct);
        var qbitTask = qbit.GetTorrentMap(seriesCard.Config, ct);
        await Task.WhenAll(detailTask, qbitTask);
        var detail = detailTask.Result with { MediaType = "tv" };
        var torrents = qbitTask.Result;
        var list = new List<(Card, State)>();
        foreach (var s in (detail.Seasons ?? []).Where(s => s.SeasonNumber > 0 && s.EpisodeCount > 0).OrderBy(s => s.SeasonNumber)) {
            var id = DeriveId($"{seriesCard.Owner:N}/{seriesCard.Account}/tv/{detail.Id}/season/{s.SeasonNumber}");
            var overview = !string.IsNullOrWhiteSpace(s.Overview)
                ? s.Overview
                : $"{s.EpisodeCount} {(s.EpisodeCount == 1 ? "episode" : "episodes")}" + (string.IsNullOrWhiteSpace(s.AirDate) ? "." : $" · Aired {s.AirDate}.");
            var seasonCard = new Card(id, seriesCard.Owner, seriesCard.Config, seriesCard.Account, detail, s.SeasonNumber,
                ParentSeriesId: seriesCard.Id, SeasonPosterPath: s.PosterPath, SeasonOverview: overview);
            Save(id, seasonCard, TimeSpan.FromHours(1));
            var state = Status(seasonCard, detail, true, torrents);
            list.Add((seasonCard, state));
        }
        return list.ToArray();
    }

    internal State Status(Card card, Title data, bool detail, IReadOnlyDictionary<string, QBitTorrent>? torrents = null)
    {
        var state = card.ReadState(data, detail, torrents);
        if (state.Covered || !state.Allowed) return state;
        return _cache.Get<State>((card.Config, card.Type, card.Title.Id, card.Season, "submitted")) ?? state;
    }

    internal async Task<State> Act(Card card, bool submit, CancellationToken ct)
    {
        if (card.StandaloneTorrent is not null) {
            var map = await qbit.GetTorrentMap(card.Config, ct);
            return card.ReadState(card.Title, true, map);
        }
        var gate = Gate((card.Type, card.Title.Id));
        await gate.WaitAsync(ct);
        try {
            var detailTask = seerr.Details(card, ct);
            var qbitTask = qbit.GetTorrentMap(card.Config, ct);
            await Task.WhenAll(detailTask, qbitTask);
            var detail = detailTask.Result;
            var torrents = qbitTask.Result;
            if (detail.Id != card.Title.Id) throw new ShelfError(502, "Seerr returned an unexpected title.");
            var state = Status(card, detail, true, torrents);
            if (!submit || state.Covered) return state;
            if (!state.Allowed) throw new ShelfError(409, state.Label);
            var receipt = 2;
            if (card.Config.DirectArrRequests) {
                await arr.Submit(card, detail, ct);
            } else {
                receipt = await seerr.Submit(card, ct);
            }
            if (receipt == 1) {
                state = new("Pending approval", true, true, "Submitted to Seerr and awaiting admin approval.");
            } else if (detail.IsUnreleased) {
                var rel = detail.FormattedReleaseDate is { } d
                    ? $"Not released yet (releases {d}). Requested and monitored for automatic download."
                    : "Not released yet. Requested and monitored for automatic download.";
                var shortLabel = detail.FormattedReleaseDate is { } rd ? $"Unreleased ({rd})" : "Unreleased";
                state = new(shortLabel, true, true, rel);
            } else {
                var target = card.Config.DirectArrRequests ? (card.Type == "movie" ? "Radarr" : "Sonarr") : "Radarr/Sonarr";
                state = new("Searching", true, true, $"Sent to {target}! Searching for a release now.");
            }
            Save((card.Config, card.Type, card.Title.Id, card.Season, "submitted"), state, TimeSpan.FromSeconds(20));
            return state;
        } finally { gate.Release(); }
    }

    internal async Task<DownloadEntry[]> GetDownloads(Configuration c, Guid owner, CancellationToken ct)
    {
        var qbitTask = qbit.GetTorrentMap(c, ct);
        SeerrRequest[] requests = [];
        int account = 0;

        if (!string.IsNullOrWhiteSpace(c.SeerrUrl) && !string.IsNullOrWhiteSpace(c.ApiKey)) {
            try {
                account = await ResolveAccount(c, owner, ct);
                var reqKey = (c, account, "requests");
                if (!_cache.TryGetValue(reqKey, out SeerrRequest[]? cachedReqs) || cachedReqs is null) {
                    cachedReqs = await seerr.Requests(c, account, ct);
                    Save(reqKey, cachedReqs, TimeSpan.FromSeconds(8));
                }
                requests = cachedReqs;
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            } catch {
                // Keep qBittorrent results usable even if Seerr is temporarily unreachable.
            }
        }

        var torrents = await qbitTask;
        var matchedHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<DownloadEntry>();

        var activeRequests = requests
            .Where(r => r.Media is { TmdbId: > 0, MediaType: "movie" or "tv" } && r.Status is 1 or 2
                && (r.Media.Status != 5 || (r.Media.DownloadStatus?.Length ?? 0) > 0))
            .DistinctBy(r => (r.Media!.MediaType, r.Media.TmdbId,
                r.Media.MediaType == "tv" && r.Seasons is { Length: 1 } ? r.Seasons[0].SeasonNumber : 0))
            .Take(25)
            .ToArray();

        using var sem = new SemaphoreSlim(6, 6);
        var fetched = await Task.WhenAll(activeRequests.Select(async req => {
            var media = req.Media!;
            var mediaType = media.MediaType!;
            var tmdbId = media.TmdbId;
            var season = mediaType == "tv" && req.Seasons is { Length: 1 } ? req.Seasons[0].SeasonNumber : 0;

            foreach (var d in media.DownloadStatus ?? []) {
                if (!string.IsNullOrWhiteSpace(d.DownloadId))
                    lock (matchedHashes) matchedHashes.Add(d.DownloadId);
            }

            var metaKey = (c, mediaType, tmdbId, "meta");
            if (!_cache.TryGetValue(metaKey, out Title? title) || title is null) {
                await sem.WaitAsync(ct);
                try {
                    if (!_cache.TryGetValue(metaKey, out title) || title is null) {
                        title = await seerr.Details(c, account, mediaType, tmdbId, ct) with { MediaType = mediaType };
                        Save(metaKey, title, TimeSpan.FromSeconds(45));
                    }
                } catch {
                    return null;
                } finally { sem.Release(); }
            }

            // Merge freshest queue info from the request object if present.
            if (media.DownloadStatus is { Length: > 0 } && (title.MediaInfo?.DownloadStatus?.Length ?? 0) == 0) {
                var mergedInfo = (title.MediaInfo ?? new MediaInfo(media.Status, null, null, null)) with {
                    DownloadStatus = media.DownloadStatus
                };
                title = title with { MediaInfo = mergedInfo };
            }

            var id = DeriveId($"{owner:N}/{account}/{mediaType}/{tmdbId}/{season}");
            var card = new Card(id, owner, c, account, title, season);
            Save(id, card, TimeSpan.FromHours(1));
            var state = Status(card, title, true, torrents);

            int group = state.Progress is { IsActive: true } ? 0
                : state.Label.StartsWith("Unreleased", StringComparison.OrdinalIgnoreCase) ? 2
                : 1;
            double sortProg = state.Progress?.Percent ?? -1;
            return new DownloadEntry(card, state, group, sortProg, title.FormattedReleaseDate);
        }));

        foreach (var item in fetched) {
            if (item is not null) entries.Add(item);
        }

        // Also include active qBittorrent torrents not yet matched to a Seerr request.
        var allowedCats = QBit.AllowedCategories(c);
        foreach (var t in torrents.Values
            .Where(t => t.IsActiveDownload && !string.IsNullOrWhiteSpace(t.Hash) && !matchedHashes.Contains(t.Hash!))
            .Where(t => allowedCats.Count == 0 || allowedCats.Contains(t.Category ?? ""))
            .OrderByDescending(t => t.AddedOn)
            .Take(20))
        {
            var title = new Title(0, "movie", t.Name ?? t.Hash,
                $"Active torrent in qBittorrent{(string.IsNullOrWhiteSpace(t.Category) ? "" : $" ({t.Category})")}.",
                null, null, null, false, null, null);
            var id = DeriveId($"{owner:N}/qbit/{t.Hash}");
            var card = new Card(id, owner, c, account, title, 0, t);
            Save(id, card, TimeSpan.FromHours(1));
            var state = card.ReadState(title, true, torrents);
            entries.Add(new DownloadEntry(card, state, 0, state.Progress?.Percent ?? 0, null));
        }

        return entries
            .OrderBy(e => e.SortGroup)
            .ThenByDescending(e => e.SortProgress)
            .ThenBy(e => e.Card.Title.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void Dispose() { _cache.Dispose(); foreach (var gate in _gates) gate.Dispose(); }
}
