using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace RequestShelf;

public sealed record PlexUserChoice(
    [property: JsonPropertyName("seerrUserId")] int SeerrUserId,
    [property: JsonPropertyName("plexId")] long PlexId,
    [property: JsonPropertyName("name")] string Name);

public sealed record QBitTestResult(
    [property: JsonPropertyName("totalTorrents")] int TotalTorrents,
    [property: JsonPropertyName("activeDownloads")] int ActiveDownloads);

public sealed record DownloadViewItem(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("mediaType")] string MediaType,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("year")] int? Year,
    [property: JsonPropertyName("posterUrl")] string? PosterUrl,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("percent")] double? Percent,
    [property: JsonPropertyName("speed")] string? Speed,
    [property: JsonPropertyName("eta")] string? Eta,
    [property: JsonPropertyName("seeders")] int? Seeders,
    [property: JsonPropertyName("size")] string? Size,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("isActive")] bool IsActive,
    [property: JsonPropertyName("releaseDate")] string? ReleaseDate);

public sealed record DownloadsSnapshot(
    [property: JsonPropertyName("qbitConfigured")] bool QBitConfigured,
    [property: JsonPropertyName("items")] DownloadViewItem[] Items);

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("RequestShelf/Accounts")]
public sealed class AccountsController(Seerr seerr) : ControllerBase
{
    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<List<PlexUserChoice>>> GetAccounts(CancellationToken ct)
    {
        try {
            return await seerr.PlexUsers(Plugin.Instance.Configuration, ct);
        } catch (ShelfError ex) {
            return Problem(statusCode: ex.Status, detail: ex.Message);
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            return Problem(statusCode: 504, detail: "Seerr took too long to list its users.");
        } catch (Exception ex) when (ex is HttpRequestException or JsonException or FormatException) {
            return Problem(statusCode: 502, detail: "Could not read Seerr's user list. Check the saved address and API key.");
        }
    }
}

[ApiController]
[Route("RequestShelf")]
public sealed class DownloadsController(CardStore store, QBit qbit) : ControllerBase
{
    [HttpPost("TestQBit")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<QBitTestResult>> TestQBit(CancellationToken ct)
    {
        try {
            var (total, active) = await qbit.TestConnection(Plugin.Instance.Configuration, ct);
            return new QBitTestResult(total, active);
        } catch (ShelfError ex) {
            return Problem(statusCode: ex.Status, detail: ex.Message);
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            return Problem(statusCode: 504, detail: "qBittorrent took too long to respond.");
        } catch (Exception ex) when (ex is HttpRequestException or JsonException or FormatException) {
            return Problem(statusCode: 502, detail: "Could not reach qBittorrent. Check the saved address and credentials.");
        }
    }

    [HttpGet("Downloads")]
    [Authorize]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<DownloadsSnapshot>> GetDownloads(CancellationToken ct)
    {
        var config = Plugin.Instance.Configuration;
        if (!Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var owner)
            || owner == Guid.Empty || !(config.EnabledUsers ?? []).Contains(owner))
            return Problem(statusCode: 403, detail: "Request Shelf is disabled for this account.");

        try {
            var entries = await store.GetDownloads(config, owner, ct);
            var items = entries.Select(e => new DownloadViewItem(
                Id: e.Card.Id,
                Title: e.Card.Title.DisplayName,
                MediaType: e.Card.Type,
                Scope: e.Card.StandaloneTorrent is not null ? (e.Card.StandaloneTorrent.Category ?? "Torrent") : e.Card.Scope,
                Year: e.Card.Title.Year,
                PosterUrl: e.Card.WithPoster ? $"/Items/{e.Card.Id:N}/Images/Primary?tag={e.Card.ArtworkTag}" : null,
                Label: e.State.Label,
                Detail: e.State.Detail,
                Percent: e.State.Progress?.Percent,
                Speed: e.State.Progress?.Speed,
                Eta: e.State.Progress?.Eta,
                Seeders: e.State.Progress?.Seeders,
                Size: e.State.Progress?.SizeBytes is { } sz ? QBitTorrent.FormatSize(sz) : null,
                State: e.State.Progress?.State ?? (e.State.Label.StartsWith("Unreleased", StringComparison.OrdinalIgnoreCase) ? "unreleased" : "pending"),
                IsActive: e.State.Progress?.IsActive ?? false,
                ReleaseDate: e.ReleaseDate)).ToArray();
            return new DownloadsSnapshot(QBit.IsConfigured(config), items);
        } catch (ShelfError ex) {
            return Problem(statusCode: ex.Status, detail: ex.Message);
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            return Problem(statusCode: 504, detail: "Timed out while fetching live download progress.");
        } catch (Exception ex) when (ex is HttpRequestException or JsonException or FormatException) {
            return Problem(statusCode: 502, detail: "Could not read download status from Seerr or qBittorrent.");
        }
    }
}
