namespace Compressr.Core;

public sealed record DvdMedia(int Type, bool RecorderSupported, bool MediaSupported,
    bool PhysicallyBlank, bool HeuristicallyBlank, long TotalSectors, long FreeSectors)
{
    public string Name => Type switch
    {
        5 => "DVD-RAM", 6 => "DVD+R", 7 => "DVD+RW", 8 => "DVD+R DL",
        9 => "DVD-R", 10 => "DVD-RW", 11 => "DVD-R DL", 13 => "DVD+RW DL",
        4 => "DVD-ROM", 0 => "No disc / unknown",
        _ => $"Non-DVD media (IMAPI type {Type})"
    };
    public long AvailableBytes => checked(FreeSectors * 2048);
    public bool Blank => PhysicallyBlank || HeuristicallyBlank;
    public string Summary => $"{Name} • {(Blank ? "blank" : "not blank")} • " +
        $"{AvailableBytes:N0} bytes available ({AvailableBytes / 1_000_000_000.0:F3} GB)";

    public void Check(long requiredBytes)
    {
        if (!RecorderSupported || !MediaSupported)
            throw new InvalidOperationException("Insert a supported writable DVD in the selected drive.");
        if (Type == 5)
            throw new InvalidOperationException("DVD-RAM is not supported for ordinary DVD-player output. Use DVD-R/+R or a player-compatible DVD-RW/+RW instead.");
        if (Type is not (6 or 7 or 8 or 9 or 10 or 11 or 13))
            throw new InvalidOperationException("The inserted medium is not a supported recordable DVD.");
        if (!Blank)
            throw new InvalidOperationException("The DVD is not blank. Compressr never erases or overwrites discs.");
        if (TotalSectors <= 0 || FreeSectors <= 0 || FreeSectors > TotalSectors)
            throw new InvalidOperationException("The drive reported invalid DVD capacity. Refresh or reconnect the drive before trying again.");
        PreparedDvd.CheckCapacity(requiredBytes, AvailableBytes);
    }
}
