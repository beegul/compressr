namespace Compressr.Core;

public sealed record DiscReadiness(bool Ready, string Detail)
{
    public static DiscReadiness NoWriter { get; } = new(false, "No DVD writer found. Save an ISO, or connect a writer and refresh.");

    public static DiscReadiness Assess(DvdMedia media, long requiredBytes)
    {
        try { media.Check(requiredBytes); return new(true, $"Ready to burn: {media.Summary}"); }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or OverflowException)
        { return new(false, $"Not ready: {ex.Message}"); }
    }

    public static DiscReadiness Failed(string detail) => new(false, $"Disc detection failed: {detail} Refresh to try again. File output is available.");
}

public sealed class DvdOutputSelection
{
    public OutputKind Selected { get; private set; } = OutputKind.Iso;
    public bool IsManual { get; private set; }

    public void Choose(OutputKind output)
    {
        if (!Enum.IsDefined(output)) throw new ArgumentOutOfRangeException(nameof(output));
        Selected = output;
        IsManual = true;
    }

    public void Apply(DiscReadiness readiness)
    {
        if (!IsManual) Selected = readiness.Ready ? OutputKind.Burn : OutputKind.Iso;
    }

    public void UseAutomatic(DiscReadiness readiness)
    {
        IsManual = false;
        Apply(readiness);
    }
}
