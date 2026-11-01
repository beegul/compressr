using System.Globalization;
using System.Text.Json;

namespace Compressr.Core;

public static class DownloadProgress
{
    public const string DownloadPrefix = "COMPRESSR_DOWNLOAD:";
    public const string ProcessPrefix = "COMPRESSR_POSTPROCESS:";

    public static StageProgress? Parse(string line, string source)
    {
        bool processing = line.StartsWith(ProcessPrefix, StringComparison.Ordinal);
        if (!processing && !line.StartsWith(DownloadPrefix, StringComparison.Ordinal))
        {
            if (line.StartsWith("[Merger]", StringComparison.Ordinal))
                return new("Merge", null, $"{source}\nCombining downloaded video and audio. Downloading is complete; merging has no percentage.");
            return null;
        }
        using var json = JsonDocument.Parse(line[(processing ? ProcessPrefix.Length : DownloadPrefix.Length)..]);
        var value = json.RootElement;
        string status = value.TryGetProperty("status", out var state) ? state.GetString() ?? "" : "";
        if (processing)
            return new("Merge", null, $"{source}\nPost-processing video/audio: {status}. Percentage unavailable.");
        double? Number(string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number &&
            property.TryGetDouble(out double number) && double.IsFinite(number) && number >= 0 ? number : null;
        double? downloaded = Number("downloaded_bytes");
        double? total = Number("total_bytes");
        bool estimated = total == null;
        total ??= Number("total_bytes_estimate");
        double? fraction = downloaded != null && total > 0 ? Math.Clamp(downloaded.Value / total.Value, 0, 1) : null;
        string Bytes(double bytes) => bytes >= 1_000_000_000 ? $"{bytes / 1_000_000_000:F2} GB" : $"{bytes / 1_000_000:F1} MB";
        string detail = fraction != null ? $"{fraction * 100:F1}% · " : "";
        detail += downloaded != null ? Bytes(downloaded.Value) : "Downloaded size unavailable";
        detail += total > 0 ? $" / {(estimated ? "~" : "")}{Bytes(total.Value)}" : " · total size unknown";
        if (Number("speed") is { } speed && speed > 0) detail += $" · {Bytes(speed)}/s";
        if (Number("eta") is { } eta && eta <= TimeSpan.MaxValue.TotalSeconds)
        {
            var remaining = TimeSpan.FromSeconds(eta);
            detail += " · ETA " + (remaining.TotalHours >= 1
                ? remaining.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
                : remaining.ToString(@"m\:ss", CultureInfo.InvariantCulture));
        }
        if (status == "finished") detail += " · stream downloaded; next stream or merge may follow";
        return new("Download", fraction, $"{source}\nCurrent stream: {detail}");
    }
}
