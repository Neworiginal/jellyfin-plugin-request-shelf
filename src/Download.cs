namespace RequestShelf;

internal sealed record QueueEpisode(int SeasonNumber);

internal sealed record DownloadProgress(
    string ShortLabel,
    string Detail,
    double? Percent,
    string? Speed,
    string? Eta,
    int? Seeders,
    long? SizeBytes,
    long? LeftBytes,
    string State,
    bool IsActive);

internal sealed record Download(
    string? DownloadId,
    decimal? Size,
    decimal? SizeLeft,
    string? Status,
    string? TimeLeft,
    string? Title,
    QueueEpisode? Episode)
{
    internal static DownloadProgress? Describe(IEnumerable<Download> queue, IReadOnlyDictionary<string, QBitTorrent>? qbit = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = queue.Where(f => string.IsNullOrWhiteSpace(f.DownloadId) || seen.Add(f.DownloadId)).ToArray();
        if (files.Length == 0) return null;

        if (qbit is { Count: > 0 }) {
            var matched = files
                .Where(f => !string.IsNullOrWhiteSpace(f.DownloadId) && qbit.ContainsKey(f.DownloadId!))
                .Select(f => qbit[f.DownloadId!])
                .DistinctBy(t => t.Hash, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (matched.Length > 0)
                return DescribeTorrents(matched);
        }

        bool All(string status) => files.All(f => string.Equals(f.Status, status, StringComparison.OrdinalIgnoreCase));
        bool Any(string status) => files.Any(f => string.Equals(f.Status, status, StringComparison.OrdinalIgnoreCase));

        if (Any("failed"))
            return new("Download failed", "Download failed in Radarr/Sonarr.", null, null, null, null, null, null, "failed", false);

        var sizesKnown = files.All(f => f.Size is > 0 && f.SizeLeft >= 0 && f.SizeLeft <= f.Size);
        if (All("completed") || (sizesKnown && files.All(f => f.SizeLeft == 0)))
            return new("Downloaded · Importing", "Download finished and is awaiting import into Jellyfin.", 100, null, null, null, null, 0, "importing", true);

        var label = All("paused") ? "Paused"
            : All("queued") ? "Queued"
            : Any("warning") ? "Download warning"
            : Any("delay") ? "Delayed"
            : All("importing") ? "Importing"
            : "Downloading";

        var eta = files.Select(f => f.TimeLeft).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t) && t != "00:00:00");
        if (!sizesKnown) {
            var detail = eta is not null ? $"{label} · ETA {eta}." : $"{label}.";
            return new(label, detail, null, null, eta, null, null, null, label.ToLowerInvariant(), true);
        }

        var size = files.Sum(f => f.Size!.Value);
        var left = files.Sum(f => f.SizeLeft!.Value);
        var pct = (double)decimal.Floor(100m * (size - left) / size);
        var shortLabel = eta is not null ? $"{label} {pct:0}% ({eta})" : $"{label} {pct:0}%";
        var detailText = eta is not null ? $"{label} {pct:0}% · ETA {eta}." : $"{label} {pct:0}%.";
        return new(shortLabel, detailText, pct, null, eta, null, (long)size, (long)left, label.ToLowerInvariant(), true);
    }

    internal static DownloadProgress DescribeTorrents(IReadOnlyList<QBitTorrent> torrents)
    {
        if (torrents.Any(t => t.IsError))
            return new("Download error", "qBittorrent reported an error on this torrent.", null, null, null, null, null, null, "error", false);

        if (torrents.All(t => t.IsCompleted)) {
            var totalSize = torrents.Sum(t => t.Size);
            return new("Downloaded · Importing",
                $"Download complete ({QBitTorrent.FormatSize(totalSize)}) · awaiting import into Jellyfin.",
                100, null, null, torrents.Sum(t => t.NumSeeds), totalSize, 0, "importing", true);
        }

        var totalBytes = torrents.Sum(t => Math.Max(0, t.Size));
        var leftBytes = torrents.Sum(t => Math.Max(0, t.AmountLeft));
        double pct = totalBytes > 0
            ? Math.Clamp(100.0 * (totalBytes - leftBytes) / totalBytes, 0, 100)
            : Math.Clamp(torrents.Average(t => t.Progress) * 100.0, 0, 100);
        var rounded = Math.Floor(pct);

        var speedBytes = torrents.Sum(t => Math.Max(0, t.DlSpeed));
        var speed = QBitTorrent.FormatSpeed(speedBytes);
        var activeEtas = torrents.Where(t => !t.IsCompleted && t.Eta > 0 && t.Eta < 8_640_000).Select(t => t.Eta).ToArray();
        var eta = activeEtas.Length > 0 ? QBitTorrent.FormatEta(activeEtas.Max()) : null;
        var seeds = torrents.Sum(t => Math.Max(0, t.NumSeeds));

        var primary = torrents.FirstOrDefault(t => t.NormalizedState is "downloading" or "forcedDL")
            ?? torrents.FirstOrDefault(t => !t.IsCompleted)
            ?? torrents[0];
        var stateLabel = primary.StateLabel;

        var shortLabel = primary.NormalizedState is "metaDL" or "forcedMetaDL"
            ? stateLabel
            : eta is not null
                ? $"{stateLabel} {rounded:0}% ({eta})"
                : $"{stateLabel} {rounded:0}%";

        var parts = new List<string> { $"{stateLabel} {rounded:0}%" };
        if (speed is not null) parts.Add(speed);
        if (eta is not null) parts.Add($"ETA {eta}");
        parts.Add($"{seeds} {(seeds == 1 ? "seeder" : "seeders")}");
        if (totalBytes > 0)
            parts.Add($"{QBitTorrent.FormatSize(totalBytes - leftBytes)} / {QBitTorrent.FormatSize(totalBytes)}");

        return new(shortLabel, string.Join(" · ", parts) + ".", rounded, speed, eta, seeds,
            totalBytes > 0 ? totalBytes : null, leftBytes, primary.NormalizedState, true);
    }

    internal static string? Label(IEnumerable<Download> queue) => Describe(queue)?.ShortLabel;
}
