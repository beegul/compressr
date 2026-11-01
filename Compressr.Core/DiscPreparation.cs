namespace Compressr.Core;

public sealed record DiscInspection(DvdMedia Media, bool EraseSupported, string? MediaIdentity, string? EraseUnavailable = null)
{
    public bool NeedsErase => !Media.Blank || (Media.CurrentStatus & 0x5800) != 0;

    public void CheckForPreparation(long requiredBytes)
    {
        if (!NeedsErase) { Media.Check(requiredBytes); return; }
        if (!Media.RecorderSupported || Media.Type is not (6 or 7 or 8 or 9 or 10 or 11 or 13))
            throw new InvalidOperationException("Insert a supported writable DVD, then check disc again.");
        if (Media.TotalSectors <= 0 || Media.FreeSectors < 0 || Media.FreeSectors > Media.TotalSectors)
            throw new InvalidOperationException("The drive reported invalid DVD capacity. No disc was erased.");
        PreparedDvd.CheckCapacity(requiredBytes, checked(Media.TotalSectors * 2048));
        if (Media.Type is not (7 or 10 or 13))
            throw new InvalidOperationException($"{Media.Name} is write-once and is not blank. It cannot be erased or reused. Insert a new blank DVD.");
        if (Media.CurrentStatus == 0 || (Media.CurrentStatus & 0xA400) != 0)
            throw new InvalidOperationException("The rewritable DVD has an unknown, damaged, write-protected or unsupported status. No disc was erased. Use another DVD.");
        if (!EraseSupported || string.IsNullOrEmpty(MediaIdentity))
            throw new InvalidOperationException($"This rewritable DVD cannot be safely prepared: {EraseUnavailable ?? "the recorder does not support erasing or media-change detection"}. No disc was erased.");
    }

    public void CheckUnchanged(DiscInspection current, long requiredBytes)
    {
        current.CheckForPreparation(requiredBytes);
        if (string.IsNullOrEmpty(MediaIdentity) || MediaIdentity != current.MediaIdentity ||
            Media != current.Media || !current.NeedsErase)
            throw new InvalidOperationException("The disc changed after inspection. Nothing was erased. Check the disc and confirm preparation again.");
    }
}

public enum DiscPreparationResult { Ready, ErasedAndReady, Declined }

public sealed class DiscPreparation
{
    private int active;

    public async Task<DiscPreparationResult> RunAsync(long requiredBytes,
        Func<CancellationToken, Task<DiscInspection>> inspect,
        Func<DiscInspection, bool> confirmErase,
        Func<DiscInspection, CancellationToken, Task> erase,
        CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0)
            throw new InvalidOperationException("A disc preparation is already running. Wait for it to finish.");
        try
        {
            token.ThrowIfCancellationRequested();
            var before = await inspect(token);
            token.ThrowIfCancellationRequested();
            before.CheckForPreparation(requiredBytes);
            if (!before.NeedsErase) return DiscPreparationResult.Ready;
            if (!confirmErase(before)) return DiscPreparationResult.Declined;
            token.ThrowIfCancellationRequested();
            before.CheckUnchanged(await inspect(token), requiredBytes);
            token.ThrowIfCancellationRequested();
            await erase(before, token);
            var after = await inspect(CancellationToken.None);
            after.Media.Check(requiredBytes);
            return DiscPreparationResult.ErasedAndReady;
        }
        finally { Volatile.Write(ref active, 0); }
    }
}
