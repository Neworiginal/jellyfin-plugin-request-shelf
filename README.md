# Request Shelf for Jellyfin 12.4

Request a movie or TV season from Jellyfin's global Search. Search for a title such as `Dune`. Your playable Jellyfin matches appear first, followed by request cards for titles missing from your library. Open a request card and select **Favorite** (the heart). Seerr receives the request under the viewer's selected Plex account or automatically linked Jellyfin account. Its request permissions, quotas, approvals and configured Radarr/Sonarr destinations apply.

## Install and receive updates

Use a Jellyfin Server running version 12.4. In Jellyfin Web open **Dashboard → Plugins → Repositories**, add **Request Shelf**, and paste the raw URL of this repository’s `manifest.json` on `main`. For a public repository named `neworiginal/jellyfin-plugin-request-shelf`, the URL after publication is:

```text
https://raw.githubusercontent.com/neworiginal/jellyfin-plugin-request-shelf/main/manifest.json
```

1. Under **Dashboard → Plugins → Catalog**, open **Request Shelf**, select version **1.2.0.0** and install it. This one-time catalog install migrates a manually installed 1.0 or 1.1, whose metadata disabled automatic updates. Restart Jellyfin to load the new version.
2. Future versions come from this feed through **Dashboard → Scheduled Tasks → Update Plugins**. Restart Jellyfin after it stages an update. Your Seerr connection and viewer pairings are stored separately from the plugin.
3. For a manual install using the downloadable full ZIP, stop Jellyfin, move your old `RequestShelf_1.*` plugin folder outside the plugins directory, then copy the entire `RequestShelf_1.2.0.0` folder into it. Keep the adjacent `configurations` folder. On Unraid, the plugin directory is under the Jellyfin container’s appdata mapping, often `plugins` or `data/plugins`. Use the **Program data path** shown in Jellyfin’s startup log to find it. Start Jellyfin again. Add the repository when available to receive updates.

## Connect to Seerr on first install

1. In Jellyfin Web, open **Dashboard → Plugins → My Plugins → Request Shelf → Settings**.
2. Enter the Seerr address reachable **from the Jellyfin container**. On a shared user-defined Docker network this can be `http://seerr:5055` if the container is named `seerr`. Alternatively use your Unraid LAN IP and Seerr's published host port, for example `http://192.168.1.50:5055`. Include your base path if you use one. Separate containers on a bridge network have separate loopback addresses.
3. Copy the API key from **Seerr → Settings → General** and click **Save**. Request Shelf loads Seerr's existing Plex users.
4. Check the box beside a Jellyfin viewer. Select **that same person's existing Plex account** in the dropdown underneath and click **Save**. Repeat for other viewers. Each request uses the selected account's Seerr permissions, approvals and quota. The dropdown includes the Seerr user number so administrators can distinguish similar names. Use **Automatic (linked Jellyfin user)** for viewers already linked through a Jellyfin-backed Seerr instance.

Keep Seerr configured for the media server you use. Request Shelf checks the viewer’s Jellyfin library for existing titles by media type and TMDB ID, using matching title and year for untagged search matches. Seerr supplies request and availability states from its own linked media server. A difference between the scanned Plex and Jellyfin libraries can produce an “Available according to Seerr” card. Selected viewers can browse Seerr's catalog, including titles outside their Jellyfin library restrictions.

## Using the remote or phone

| Enter in Jellyfin's global Search | Select Favorite to request |
| --- | --- |
| `Dune` | The movie on the card you select. The year distinguishes releases. |
| `Severance` | Season 1 of the selected series. |
| `Severance s2` | Season 2. |
| `Severance all seasons` | All currently listed regular seasons with episodes that Seerr can request. |

Use Search **inside Jellyfin** on Roku or Fire TV. Roku receives movie-style cards for shows so the Favorite button is available. The season appears in the card title, and the request still goes to Sonarr. On Jellyfin Web and Fire TV, series results use series cards. Search returns the first Seerr results page. Narrow the title or add a year if needed.

The optional `req Dune` form searches only the Seerr catalog. Use a season suffix even for a series already present in Jellyfin.

Opening results only browses the catalog. Press the heart on a **Request** card to submit. Reopen the card to see Seerr's current state, particularly on Roku where the heart can change before a failed request is reported. Use Seerr to manage or cancel requests. After a download imports, search normally to play the Jellyfin library copy.

## Download progress

A request card can show **Pending approval**, **Processing**, **Downloading 42%**, or **Downloaded · Awaiting import**. Reopen the card to get the latest queue state. Search results can be cached for 12 seconds. Once Jellyfin scans the import, normal search opens the playable library item.

Progress uses the regular Radarr or Sonarr queue Seerr already tracks. Under **Seerr → Settings → Services**, edit those connections and turn on **Enable Scan**. qBittorrent credentials stay in Radarr and Sonarr. When a release has not entered the queue, or transfer sizes are unavailable, the card shows the status Seerr can report. For TV, the percentage covers active transfers for the season selected. A season pack counts once. Use `all seasons` to include transfers whose season number Seerr has not supplied.

## If the first request doesn't arrive

- **Plugin missing after restart.** Check the startup log for `Loaded plugin: Request Shelf 1.2.0.0`. Verify the server version is 12.4 and the installed path ends in `plugins/RequestShelf_1.2.0.0/Jellyfin.Plugin.RequestShelf.dll`.
- **Empty search or access denied.** Try `req Dune` in global Search. Confirm that the viewer is checked in the plugin, their Plex pairing is selected and saved (or their Jellyfin account is linked in Automatic mode), and the plugin address resolves inside the Jellyfin container.
- **Heart changes but no request appears.** Reopen the card and check Jellyfin's log for `Request Shelf returned`. Check the viewer's Seerr permissions and quota, and whether the title is already available or pending. If Seerr disables partial series requests, search with `all seasons`.
- **Request is in Seerr but nothing downloads.** Check its approval state and Seerr's default Radarr or Sonarr configuration. Prowlarr and qBittorrent continue to use your existing settings.
- **An old card fails to open.** Search again. Cards expire after one hour and also clear on restart or a settings change.

## Compatibility and verification

Built for Jellyfin Server 12.4 on .NET 10. The test used an actual Jellyfin 12.4 server with a simulated Seerr API. It covered admin settings, search, movie and season submission, approval status, repeated heart presses, disabled users, user isolation, and denied requests. The 1.1 tests include choosing an existing Plex account in the admin page, reading the saved pairing after a reload, preserving Seerr attribution, and rejecting an obsolete pairing. The Jellyfin Web flow also passed in Chromium at a phone viewport. Roku 3.2.4 and Jellyfin for Android TV 0.20.0 search/detail/favorite paths were checked against their released source and exercised at the server API. Physical clients and a live Unraid download were not tested.

The 1.2 integration test scanned a test video cataloged as *Dune* (2021). A phone-width Jellyfin Web search showed that local copy alongside a download card for the missing 1984 film. Installing 1.2 from a local test feed on a 1.1 server preserved the connection and account pairings across restart. The test covered ordinary search, pagination, search hints, account restrictions, upstream failure, and movie and season progress from a simulated Seerr queue.

## Publishing a version

The repository contains both the C# source and the Jellyfin feed. On `main`, bump the four-part version in `src/RequestShelf.csproj` and update `release-notes.txt`. The GitHub Actions workflow builds the DLL and commits the versioned ZIP and updated `manifest.json` together. Keep the feed and packages publicly readable. Actions needs permission to write repository contents for future releases. The first package and feed are already built.

For a local release, use the .NET 10 SDK and Python 3. From the repository root:

```sh
dotnet build src/RequestShelf.csproj -c Release
python3 scripts/package.py --repo OWNER/REPOSITORY
```

Commit `packages/` and `manifest.json` together. The packager verifies the checksum of earlier releases and preserves their bytes. A source change needs a new version number to ship to Jellyfin servers. To remove the plugin, uninstall it in Jellyfin and restart. Seerr retains submitted requests.

[Jellyfin plugins](https://jellyfin.org/docs/general/server/plugins/) · [Jellyfin scheduled tasks](https://jellyfin.org/docs/general/server/tasks/) · [Seerr services](https://docs.seerr.dev/using-seerr/settings/services/)
