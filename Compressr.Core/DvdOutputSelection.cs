namespace Compressr.Core;

public sealed record DiscReadiness(bool Ready, string Detail, bool NeedsPreparation = false, bool DetectionFailed = false)
{
    public static DiscReadiness NoWriter { get; } = new(false, "No DVD writer found. File output is available; connected writers are detected automatically.");

    public static DiscReadiness Assess(DiscInspection inspection, long requiredBytes)
    {
        if (!inspection.NeedsErase) return Assess(inspection.Media, requiredBytes);
        if (inspection.Media.Type is 7 or 10 or 13 && string.IsNullOrEmpty(inspection.MediaIdentity) &&
            !string.IsNullOrWhiteSpace(inspection.EraseUnavailable))
            return Failed(inspection.EraseUnavailable);
        try
        {
            inspection.CheckForPreparation(requiredBytes);
            return new(false, $"{inspection.Media.Name}: Make DVD can prepare this disc. Erasing removes ALL contents and requires separate permission.", true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or OverflowException)
        { return new(false, $"Not ready: {ex.Message}"); }
    }

    public static DiscReadiness Assess(DvdMedia media, long requiredBytes)
    {
        try { media.Check(requiredBytes); return new(true, $"Ready to burn: {media.Summary}"); }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or OverflowException)
        { return new(false, $"Not ready: {ex.Message}"); }
    }

    public static DiscReadiness Failed(string detail) => new(false,
        $"Disc detection failed: {detail} Retrying automatically. You can explicitly choose file output.", DetectionFailed: true);
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
        if (!IsManual) Selected = readiness.Ready || readiness.NeedsPreparation ? OutputKind.Burn : OutputKind.Iso;
    }

    public void UseAutomatic(DiscReadiness readiness)
    {
        IsManual = false;
        Apply(readiness);
    }
}
