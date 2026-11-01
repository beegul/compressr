namespace Compressr.Core;

public sealed record PreparedImageDestinationSnapshot(
    string Path, bool Existed, long? ExistingLength, DateTime? ExistingModifiedUtc);

public static class PreparedImageDestination
{
    public const long SafetyReserveBytes = 256L * 1024 * 1024;

    public static PreparedImageDestinationSnapshot Inspect(string path, long estimatedImageBytes)
    {
        if (estimatedImageBytes < 0) throw new ArgumentOutOfRangeException(nameof(estimatedImageBytes));
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("Choose an absolute path for the DVD-ready ISO.", nameof(path));

        string fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(fullPath), ".iso", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The saved DVD image must use the .iso extension.", nameof(path));
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The DVD image path has no parent folder.", nameof(path));
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"The ISO destination folder does not exist: {directory}");
        if (Directory.Exists(fullPath))
            throw new IOException($"The ISO destination is a folder, not a file: {fullPath}");

        string root = Path.GetPathRoot(fullPath)
            ?? throw new ArgumentException("The DVD image path has no volume.", nameof(path));
        var volume = new DriveInfo(root);
        if (!volume.IsReady)
            throw new IOException($"The ISO destination volume is not ready: {root}");
        long required = checked(estimatedImageBytes + SafetyReserveBytes);
        long available = volume.AvailableFreeSpace;
        if (available < required)
            throw new IOException($"The ISO destination has {available / 1_000_000_000.0:F2} GB free; " +
                $"about {estimatedImageBytes / 1_000_000_000.0:F2} GB plus a 0.27 GB safety reserve is needed. " +
                "Choose another location or free disk space before preparing the DVD.");
        ProbeWritableDirectory(directory);

        var existing = new FileInfo(fullPath);
        return existing.Exists
            ? new(fullPath, true, existing.Length, existing.LastWriteTimeUtc)
            : new(fullPath, false, null, null);
    }

    private static void ProbeWritableDirectory(string directory)
    {
        string probe = Path.Combine(directory, $".compressr-destination-{Guid.NewGuid():N}.tmp");
        try
        {
            using var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (File.Exists(probe))
            {
                try { File.Delete(probe); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                { throw new IOException($"The ISO destination is not writable, and its temporary check could not be removed from {directory}.", new AggregateException(ex, cleanup)); }
            }
            throw new IOException($"The ISO destination folder is not writable: {directory}. {ex.Message}", ex);
        }
        if (File.Exists(probe))
        {
            try { File.Delete(probe); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new IOException($"Could not remove the temporary write check from {directory}.", ex); }
        }
    }

    public static void Commit(string partialPath, PreparedImageDestinationSnapshot destination)
    {
        string partial = Path.GetFullPath(partialPath);
        if (!string.Equals(Path.GetDirectoryName(partial), Path.GetDirectoryName(destination.Path), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The temporary ISO must be created beside its destination so it can be committed atomically.");
        if (!File.Exists(partial))
            throw new InvalidDataException("A complete DVD-ready ISO is required before it can be saved.");
        PreparedDvd.CheckCapacity(new FileInfo(partial).Length, DvdPlan.DiscCapacity(DiscSize.DualLayer));

        var current = new FileInfo(destination.Path);
        if (destination.Existed != current.Exists ||
            (current.Exists && (current.Length != destination.ExistingLength ||
                current.LastWriteTimeUtc != destination.ExistingModifiedUtc)))
            throw new IOException("The destination ISO changed during preparation. It was not overwritten. Choose another filename and prepare again.");

        File.Move(partial, destination.Path, destination.Existed);
    }
}
