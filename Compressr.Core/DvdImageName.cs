namespace Compressr.Core;

public static class DvdImageName
{
    public static string Suggest(string directory, IReadOnlyList<SourceVideo> sources)
    {
        string title = sources.Count == 0 ? "Compressr" : sources[0].IsLocal
            ? Path.GetFileNameWithoutExtension(sources[0].Input)
            : sources[0].Metadata?.Title ?? "YouTube video";
        string suffix = sources.Count > 1 ? $" and {sources.Count - 1} more" : "";
        var invalid = "<>:\"/\\|?*";
        string stem = new(title.Select(c => c < 32 || invalid.Contains(c) ? '_' : c).ToArray());
        stem = stem.Trim().TrimEnd('.', ' ');
        if (stem.Length == 0) stem = "DVD video";
        string device = stem.Split('.')[0].TrimEnd(' ');
        if (device.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            device.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            device.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            device.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (device.Length == 4 && (device.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                device.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && "123456789\u00b9\u00b2\u00b3".Contains(device[3])))
            stem = "_" + stem;
        // Leave room for the unique sibling ".<guid>.partial" used during authoring.
        int limit = Math.Min(120, 210 - Path.GetFullPath(directory).Length - 1 - suffix.Length - 4);
        if (limit < 8)
            throw new PathTooLongException("The selected ISO folder is too long for a source-based filename. Choose a shorter location.");
        if (stem.Length > limit)
        {
            stem = stem[..limit];
            if (char.IsHighSurrogate(stem[^1])) stem = stem[..^1];
        }
        return Path.Combine(directory, stem.TrimEnd('.', ' ') + suffix + ".iso");
    }
}
