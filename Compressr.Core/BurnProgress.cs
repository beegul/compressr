namespace Compressr.Core;

public sealed record BurnProgressSnapshot(int Action, long StartLba, long SectorCount, long LastWrittenLba,
    int ElapsedSeconds, int RemainingSeconds, int TotalSeconds);

public static class BurnProgress
{
    public static StageProgress Describe(BurnProgressSnapshot value)
    {
        string stage = value.Action switch
        {
            0 => "Burn · checking media",
            1 => "Burn · preparing media",
            2 => "Burn · starting writer",
            3 => "Burn · calibrating",
            4 => "Burn · writing",
            5 => "Burn · finalising",
            6 => "Burn · write complete",
            7 => "Burn · drive verification",
            _ => "Burn"
        };
        double? fraction = value.Action == 4 && value.SectorCount > 0
            ? Math.Clamp(((double)value.LastWrittenLba - value.StartLba + 1) / value.SectorCount, 0, 1)
            : null;
        string detail = fraction is { } amount ? $"{amount:P1} of image data written. " : "";
        detail += value.Action switch
        {
            0 => "Checking the inserted disc.",
            1 => "IMAPI is preparing the media as required for writing.",
            2 => "Initializing the writer.",
            3 => "Calibrating laser power for this disc.",
            4 => "Writing DVD image data.",
            5 => "Finalising the disc; data writing alone is not completion.",
            6 => "Write/finalise completed. SHA-256 read-back is still required; the disc is not yet verified.",
            7 => "The drive is checking the write. App SHA-256 read-back is still required.",
            _ => "Waiting for the writer's next progress update."
        };
        if (value.Action != 6)
        {
            if (value.ElapsedSeconds > 0) detail += $" Elapsed {Time(value.ElapsedSeconds)}.";
            detail += value.RemainingSeconds > 0 && value.TotalSeconds > 0
                ? $" About {Time(value.RemainingSeconds)} remaining (writer estimate; may change, excludes app read-back)."
                : " Time remaining is not available yet.";
        }
        return new(stage, fraction, detail + "\nDo not remove the disc. Writing and verification cannot be cancelled.");
    }

    private static string Time(int seconds) => TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss",
        System.Globalization.CultureInfo.InvariantCulture);
}
