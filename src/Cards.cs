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
        if (!suffix.Success) return new(term, 1, false);
        int season = suffix.Groups[1].Success ? int.Parse(suffix.Groups[1].Value) : 0;
        return new(term[..suffix.Index].TrimEnd(), season, true);
    }
}

internal sealed record State(string Label, bool Covered = false, bool Allowed = true, string? Detail = null);

internal sealed record Card(Guid Id, Guid Owner, Configuration Config, int Account, Title Title, int Season)
{
    public string Type => Title.MediaType!;
    public BaseItemKind Kind => Type == "movie" ? BaseItemKind.Movie : BaseItemKind.Series;
    public string Scope => Type == "movie" ? "Movie" : Season == 0 ? "All seasons" : $"Season {Season}";
    public string ArtworkTag => Id.ToString("N");
    public bool WithPoster => Seerr.HasPoster(Title.PosterPath);

    public State ReadState(Title data, bool detail)
    {
        var state = Availability(data, detail);
        var info = data.MediaInfo;
        if (!state.Allowed || info is null || info.Status == 5) return state;
        var queue = (info.DownloadStatus ?? []).Where(x => Type == "movie" || Season == 0 || x.Episode?.SeasonNumber == Season);
        var progress = Download.Label(queue);
        if (progress is null) return state;
        return new(progress, state.Covered || Type == "movie" || Season > 0, true,
            Type == "tv" ? $"{progress} for current transfers in {Scope.ToLowerInvariant()}." : progress + ".");
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
    private string Caption(State state) => $"{state.Label} · {Title.DisplayName}" + (Type == "tv" ? $" · {Scope}" : "");

    public BaseItemDto Item(string server, State state, bool roku, string? overview = null)
    {
        var instruction = state.Covered || !state.Allowed ? state.Label + "." :
            $"Press Favorite (heart) to request {(Type == "movie" ? "this movie" : Scope.ToLowerInvariant())} through Seerr.";
        return new() {
            Id = Id, ServerId = server, Name = Caption(state), OriginalTitle = Title.DisplayName,
            Type = roku ? BaseItemKind.Movie : Kind, MediaType = roku || Type == "movie" ? MediaType.Video : MediaType.Unknown,
            LocationType = LocationType.Virtual, PlayAccess = PlayAccess.None,
            IsFolder = !roku && Type == "tv", CanDelete = false, CanDownload = false, ProductionYear = Title.Year,
            Overview = (state.Detail is null ? instruction : state.Covered ? state.Detail : state.Detail + " " + instruction) + "\n\n" + (overview ?? Title.Overview ?? ""),
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

internal sealed class CardStore(Seerr seerr) : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 8192 });
    private readonly byte[] _salt = RandomNumberGenerator.GetBytes(32);
    // Stripes bound memory and serialize concurrent search rows and repeated presses for the same title.
    private readonly SemaphoreSlim[] _gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private SemaphoreSlim Gate(object key) => _gates[(uint)key.GetHashCode() % _gates.Length];

    private void Save<T>(object key, T value, TimeSpan ttl) => _cache.Set(key, value,
        new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = ttl });
    public Card? Find(Guid id) => _cache.Get<Card>(id);

    public async Task<Card[]> Search(Configuration c, Guid owner, SearchSpec spec, CancellationToken ct)
    {
        var key = (c, owner, spec);
        var gate = Gate(key);
        await gate.WaitAsync(ct);
        try {
            if (_cache.TryGetValue(key, out Card[]? cached) && cached is not null && cached.All(x => Find(x.Id) is not null)) return cached;
            var accountKey = (c, owner);
            if (!_cache.TryGetValue(accountKey, out int account)) {
                account = await seerr.AccountFor(c, owner, ct);
                Save(accountKey, account, TimeSpan.FromMinutes(2));
            }
            var titles = await seerr.Search(c, account, spec.Term, ct);
            var cards = new List<Card>();
            foreach (var title in titles.Where(t => t.MediaType is "movie" or "tv" && t.Id > 0 && !t.Adult && t.DisplayName.Length > 0)) {
                if (spec.TvOnly && title.MediaType != "tv") continue;
                var bytes = Encoding.UTF8.GetBytes($"{owner:N}/{account}/{title.MediaType}/{title.Id}/{spec.Season}");
                var id = new Guid(HMACSHA256.HashData(_salt, bytes).AsSpan(0, 16));
                var card = new Card(id, owner, c, account, title, spec.Season);
                Save(id, card, TimeSpan.FromHours(1));
                cards.Add(card);
            }
            var result = cards.ToArray();
            Save(key, result, TimeSpan.FromSeconds(12));
            return result;
        } finally { gate.Release(); }
    }

    public State Status(Card card, Title data, bool detail)
    {
        var state = card.ReadState(data, detail);
        if (state.Covered || !state.Allowed) return state;
        return _cache.Get<State>((card.Config, card.Type, card.Title.Id, card.Season, "submitted")) ?? state;
    }

    public async Task<State> Act(Card card, bool submit, CancellationToken ct)
    {
        var gate = Gate((card.Type, card.Title.Id));
        await gate.WaitAsync(ct);
        try {
            var detail = await seerr.Details(card, ct);
            if (detail.Id != card.Title.Id) throw new ShelfError(502, "Seerr returned an unexpected title.");
            var state = Status(card, detail, true);
            if (!submit || state.Covered) return state;
            if (!state.Allowed) throw new ShelfError(409, state.Label);
            var receipt = await seerr.Submit(card, ct);
            state = new(receipt == 1 ? "Pending approval" : receipt == 2 ? "Approved" : "Submitted", true);
            Save((card.Config, card.Type, card.Title.Id, card.Season, "submitted"), state, TimeSpan.FromSeconds(20));
            return state;
        } finally { gate.Release(); }
    }

    public void Dispose() { _cache.Dispose(); foreach (var gate in _gates) gate.Dispose(); }
}
