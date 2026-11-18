using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Search;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace RequestShelf;

internal sealed class RequestFilter(CardStore store, Seerr seerr, IServerApplicationHost server,
    IUserManager users, ILibraryManager library, ILogger<RequestFilter> logger) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
    {
        if (OrdinarySearch(ctx) is { } search) {
            if (ctx.RouteData.Values["controller"] as string == "Items")
                ctx.ActionArguments["enableTotalRecordCount"] = true;
            var executed = await next();
            if (executed.Exception is not null || executed.Result is not ObjectResult native ||
                native.StatusCode is >= 300 || native.Value is not (QueryResult<BaseItemDto> or SearchHintResult)) return;
            try {
                await AppendMissing(ctx, native, search);
            } catch (OperationCanceledException) when (ctx.HttpContext.RequestAborted.IsCancellationRequested) {
                throw;
            } catch (Exception ex) {
                // A failed optional catalog lookup must leave the original Jellyfin response usable.
                logger.LogDebug("Request Shelf kept native search results ({ErrorType}).", ex.GetType().Name);
            }
            return;
        }
        try {
            var result = await Handle(ctx);
            if (result is not null) {
                ctx.HttpContext.Response.Headers.CacheControl = "private, no-store";
                ctx.Result = result;
                return;
            }
        } catch (ShelfError ex) {
            ctx.Result = Error(ex.Status, ex.Message);
            logger.LogInformation("Request Shelf returned {Status}. {Reason}", ex.Status, ex.Message);
            return;
        } catch (OperationCanceledException) when (!ctx.HttpContext.RequestAborted.IsCancellationRequested) {
            ctx.Result = Error(504, "Seerr took too long. Reopen the card to check its status.");
            return;
        } catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or FormatException) {
            logger.LogWarning("Request Shelf could not read the upstream response ({ErrorType}).", ex.GetType().Name);
            ctx.Result = Error(502, "Request Shelf could not reach or read Seerr. Check the plugin settings and Seerr logs.");
            return;
        }
        await next();
    }

    private static readonly HashSet<string> GlobalSearchParameters = new(StringComparer.OrdinalIgnoreCase) {
        "searchTerm", "userId", "includeItemTypes", "excludeItemTypes", "mediaTypes", "recursive",
        "limit", "startIndex", "fields", "imageTypeLimit", "enableImages", "enableImageTypes",
        "enableUserData", "enableTotalRecordCount", "isMissing", "includeMedia", "includePeople", "includeGenres",
        "includeStudios", "includeArtists", "isMovie", "isSeries", "groupProgramsBySeries",
        "api_key", "x-emby-token"
    };

    private static (bool Movies, bool Shows) Kinds(ActionExecutingContext ctx, SearchSpec spec)
    {
        var args = ctx.ActionArguments;
        T[] Many<T>(string name) => args.TryGetValue(name, out var value) && value is T[] array ? array : [];
        bool? Flag(string name) => args.TryGetValue(name, out var value) ? value as bool? : null;
        var include = Many<BaseItemKind>("includeItemTypes");
        var exclude = Many<BaseItemKind>("excludeItemTypes");
        var media = Many<MediaType>("mediaTypes");
        bool Allows(BaseItemKind kind) => (include.Length == 0 || include.Contains(kind)) && !exclude.Contains(kind);
        return (
            Allows(BaseItemKind.Movie) && !spec.TvOnly && Flag("isMovie") != false && Flag("isSeries") != true &&
                (media.Length == 0 || media.Contains(MediaType.Video)),
            Allows(BaseItemKind.Series) && Flag("isSeries") != false && Flag("isMovie") != true &&
                (media.Length == 0 || media.Contains(MediaType.Unknown)));
    }

    private sealed record Search(Configuration Config, Guid Owner, SearchSpec Spec, bool Movies, bool Shows);

    private static Search? OrdinarySearch(ActionExecutingContext ctx)
    {
        var controller = ctx.RouteData.Values["controller"] as string;
        var action = ctx.RouteData.Values["action"] as string;
        if (!(controller == "Items" && action is "GetItems" or "GetItemsByUserIdLegacy" ||
              controller == "Search" && action == "GetSearchHints")) return null;
        var args = ctx.ActionArguments;
        if (!args.TryGetValue("searchTerm", out var raw) || raw is not string input) return null;
        var term = input.Trim();
        if (term.Length is 0 or > 180 || SearchSpec.Parse(input) is not null ||
            ctx.HttpContext.Request.Query.Keys.Any(k => !GlobalSearchParameters.Contains(k)) ||
            args.TryGetValue("includeMedia", out var media) && media is false ||
            args.TryGetValue("isMissing", out var missing) && missing is true) return null;
        var principal = ctx.HttpContext.User;
        var config = Plugin.Instance.Configuration;
        if (principal.Identity?.IsAuthenticated != true || !Guid.TryParse(principal.FindFirst("Jellyfin-UserId")?.Value, out var owner) ||
            owner == Guid.Empty || !(config.EnabledUsers ?? []).Contains(owner) ||
            args.TryGetValue("userId", out var user) && user is Guid requested && requested != owner) return null;
        var spec = SearchSpec.FromTerm(term);
        var (movies, shows) = Kinds(ctx, spec);
        return movies || shows ? new(config, owner, spec, movies, shows) : null;
    }

    private async Task AppendMissing(ActionExecutingContext ctx, ObjectResult native, Search search)
    {
        var user = users.GetUserById(search.Owner);
        if (user is null) return;
        var ct = ctx.HttpContext.RequestAborted;
        // Bound the delay added to a normal search when Seerr is slow or offline.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var found = await store.Search(search.Config, search.Owner, search.Spec, timeout.Token);
        var cards = found.Where(c => c.Type == "movie" ? search.Movies : search.Shows).DistinctBy(c => c.Id).ToArray();
        if (cards.Length == 0) return;

        // A season selection can request missing seasons for a series already on the server.
        if (!search.Spec.TvOnly) {
            InternalItemsQuery Query() => new(user) { IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
                Recursive = true, IsVirtualItem = false, EnableTotalRecordCount = false };
            var byId = Query();
            byId.HasAnyProviderIds = new() { ["Tmdb"] = cards.Select(c => c.Title.Id.ToString()).Distinct().ToArray() };
            var owned = library.GetItemList(byId).Select(i =>
                (i.GetBaseItemKind(), i.ProviderIds.GetValueOrDefault("Tmdb"))).ToHashSet();
            var byName = Query();
            byName.ExcludeProviderIds = new() { ["Tmdb"] = string.Empty };
            byName.SearchTerm = search.Spec.Term;
            var fallback = library.GetItemList(byName);
            cards = cards.Where(c => !owned.Contains((c.Kind, c.Title.Id.ToString())) &&
                !fallback.Any(i => i.GetBaseItemKind() == c.Kind && c.Title.Year.HasValue && i.ProductionYear == c.Title.Year &&
                    (string.Equals(i.Name, c.Title.DisplayName, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(i.OriginalTitle, c.Title.DisplayName, StringComparison.OrdinalIgnoreCase)))).ToArray();
        }
        int Arg(string key, int fallback) => ctx.ActionArguments.TryGetValue(key, out var v) && v is int n ? Math.Max(0, n) : fallback;
        var start = Arg("startIndex", 0);
        var limit = Arg("limit", int.MaxValue);
        IEnumerable<Card> Page(int localCount, int localTotal) =>
            cards.Skip(Math.Max(0, start - localTotal)).Take(Math.Max(0, limit - localCount));
        State Current(Card c) => store.Status(c, c.Title, false);
        bool roku = ctx.HttpContext.User.FindFirst("Jellyfin-Client")?.Value.Contains("Roku", StringComparison.OrdinalIgnoreCase) == true;
        if (native.Value is QueryResult<BaseItemDto> items) {
            var extra = Page(items.Items.Count, items.TotalRecordCount).Select(c => c.Item(server.SystemId, Current(c), roku));
            native.Value = new QueryResult<BaseItemDto>(start, items.TotalRecordCount + cards.Length, items.Items.Concat(extra).ToArray());
        } else if (native.Value is SearchHintResult hints) {
            var extra = Page(hints.SearchHints.Count, hints.TotalRecordCount).Select(c => c.Hint(Current(c)));
            native.Value = new SearchHintResult(hints.SearchHints.Concat(extra).ToArray(), hints.TotalRecordCount + cards.Length);
        }
        ctx.HttpContext.Response.Headers.CacheControl = "private, no-store";
    }

    private static ObjectResult Error(int status, string detail) =>
        new(new ProblemDetails { Status = status, Title = "Request Shelf", Detail = detail }) { StatusCode = status };

    private async Task<IActionResult?> Handle(ActionExecutingContext ctx)
    {
        var action = ctx.RouteData.Values["action"] as string;
        var controller = ctx.RouteData.Values["controller"] as string;
        var items = controller == "Items" && action is "GetItems" or "GetItemsByUserIdLegacy";
        var hints = controller == "Search" && action == "GetSearchHints";
        var args = ctx.ActionArguments;
        T? Arg<T>(string name) where T : struct => args.TryGetValue(name, out var v) && v is T t ? t : null;
        T[] Many<T>(string name) => args.TryGetValue(name, out var v) && v is T[] t ? t : [];
        var ct = ctx.HttpContext.RequestAborted;

        var spec = items || hints ? SearchSpec.Parse(args.TryGetValue("searchTerm", out var v) ? v as string : null) : null;
        Guid? id = Arg<Guid>("itemId") ?? Arg<Guid>("seriesId") ?? Arg<Guid>("parentId");
        var ids = items ? Many<Guid>("ids") : [];
        if (!id.HasValue && ids.Length == 1) id = ids[0];
        var card = id.HasValue ? store.Find(id.Value) : null;
        if (card is null && spec is null) return null;
        var config = Plugin.Instance.Configuration;
        if (card is not null && !ReferenceEquals(config, card.Config)) return new NotFoundResult();
        if (card is not null && controller == "Image" && action is "GetItemImage" or "GetItemImageByIndex" or "GetItemImage2") {
            if (!card.WithPoster || Arg<ImageType>("imageType") != ImageType.Primary) return new NotFoundResult();
            return new FileContentResult(await seerr.Poster(card.Title.PosterPath!, ct), "image/jpeg");
        }

        var principal = ctx.HttpContext.User;
        if (principal.Identity?.IsAuthenticated != true || !Guid.TryParse(principal.FindFirst("Jellyfin-UserId")?.Value, out var owner)
            || owner == Guid.Empty || !(config.EnabledUsers ?? []).Contains(owner) || (Arg<Guid>("userId") is Guid supplied && supplied != owner)
            || (card is not null && card.Owner != owner))
            return Error(403, "Request Shelf is disabled for this account.");

        // Roku 3.2.4 has a Favorite button on movie details but none on series details.
        bool roku = principal.FindFirst("Jellyfin-Client")?.Value.Contains("Roku", StringComparison.OrdinalIgnoreCase) == true;
        if (spec is not null) {
            var start = Math.Max(0, Arg<int>("startIndex") ?? 0);
            var limit = Math.Clamp(Arg<int>("limit") ?? 20, 0, 20);
            var (movies, shows) = Kinds(ctx, spec);
            var found = spec.Term.Length is > 0 and <= 180 && (movies || shows) && Arg<Guid>("parentId") is null && Arg<bool>("includeMedia") != false
                ? await store.Search(config, owner, spec, ct) : [];
            var matches = found.Where(c => c.Type == "movie" ? movies : shows).ToArray();
            var slice = matches.Skip(start).Take(limit);
            State StatusOf(Card c) => store.Status(c, c.Title, false);
            if (hints) return new OkObjectResult(new SearchHintResult(slice.Select(c => c.Hint(StatusOf(c))).ToArray(), matches.Length));
            return new OkObjectResult(new QueryResult<BaseItemDto>(start, matches.Length,
                slice.Select(c => c.Item(server.SystemId, StatusOf(c), roku)).ToArray()));
        }

        if (card is null) return null;
        if (controller == "UserLibrary" && action is "MarkFavoriteItem" or "MarkFavoriteItemLegacy" or "UnmarkFavoriteItem" or "UnmarkFavoriteItemLegacy") {
            var submit = action.StartsWith("Mark", StringComparison.Ordinal);
            return new OkObjectResult(card.UserData(await store.Act(card, submit, ct)));
        }
        if ((controller == "UserLibrary" && action is "GetItem" or "GetItemLegacy") ||
            (controller == "Items" && action is "GetItemUserData" or "GetItemUserDataLegacy") || (items && ids.Length == 1)) {
            var detail = await seerr.Details(card, ct);
            var state = store.Status(card, detail, true);
            if (controller == "Items" && !items) return new OkObjectResult(card.UserData(state));
            var item = card.Item(server.SystemId, state, roku, detail.Overview);
            return new OkObjectResult(items ? new QueryResult<BaseItemDto>([item]) : item);
        }
        if (controller == "Library" && action == "GetThemeMedia")
            return new OkObjectResult(new AllThemeMediaResult());
        if (controller == "Library" && action is "GetThemeSongs" or "GetThemeVideos")
            return new OkObjectResult(new ThemeMediaResult());
        if (controller == "TvShows" && action is "GetSeasons" or "GetEpisodes" or "GetNextUp"
            || controller == "Library" && action is "GetSimilarItems" or "GetItemCollections"
            || controller == "Videos" && action == "GetAdditionalPart" || items)
            return new OkObjectResult(new QueryResult<BaseItemDto>());
        if (controller == "UserLibrary" && action is "GetSpecialFeatures" or "GetSpecialFeaturesLegacy" or "GetLocalTrailers" or "GetLocalTrailersLegacy"
            || controller == "Library" && action == "GetAncestors")
            return new OkObjectResult(Array.Empty<BaseItemDto>());
        return null;
    }
}
