using System.Text.Json;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace RequestShelf;

public sealed record PlexUserChoice(
    [property: System.Text.Json.Serialization.JsonPropertyName("seerrUserId")] int SeerrUserId,
    [property: System.Text.Json.Serialization.JsonPropertyName("plexId")] long PlexId,
    [property: System.Text.Json.Serialization.JsonPropertyName("name")] string Name);

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
