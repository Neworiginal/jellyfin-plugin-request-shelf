namespace RequestShelf;

internal sealed record QueueEpisode(int SeasonNumber);

internal sealed record Download(string? DownloadId, decimal? Size, decimal? SizeLeft, string? Status, QueueEpisode? Episode)
{
    internal static string? Label(IEnumerable<Download> queue)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = queue.Where(f => string.IsNullOrWhiteSpace(f.DownloadId) || seen.Add(f.DownloadId)).ToArray();
        if (files.Length == 0) return null;

        bool All(string status) => files.All(f => string.Equals(f.Status, status, StringComparison.OrdinalIgnoreCase));
        if (files.Any(f => string.Equals(f.Status, "failed", StringComparison.OrdinalIgnoreCase))) return "Download failed";
        var sizesKnown = files.All(f => f.Size is > 0 && f.SizeLeft >= 0 && f.SizeLeft <= f.Size);
        if (All("completed") || (sizesKnown && files.All(f => f.SizeLeft == 0))) return "Downloaded · Awaiting import";
        var label = All("paused") ? "Paused" : All("queued") ? "Queued" : "Downloading";
        if (!sizesKnown) return label;
        var size = files.Sum(f => f.Size!.Value);
        var left = files.Sum(f => f.SizeLeft!.Value);
        return $"{label} {decimal.Floor(100m * (size - left) / size)}%";
    }
}
