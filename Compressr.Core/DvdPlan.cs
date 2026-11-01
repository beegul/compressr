using System.Globalization;

namespace Compressr.Core;

public enum TelevisionSystem { PAL, NTSC }
public enum DiscSize { SingleLayer, DualLayer }
public enum OutputKind { Burn, Iso, Folder }
public sealed record VideoInfo(double Duration, int Width, int Height, double PixelAspect, int Rotation, bool HasAudio)
{
    public double DisplayAspect(int extraRotation)
    {
        double aspect = Width * PixelAspect / Height;
        return Math.Abs((Rotation + extraRotation) % 180) == 90 ? 1 / aspect : aspect;
    }
}

public sealed record DvdPlan(TelevisionSystem System, long Capacity, int VideoBitrate, bool Wide, int Width, int Height)
{
    public const int AudioBitrate = 192_000;
    public const int MinimumBitrate = 2_000_000;
    public static long DiscCapacity(DiscSize size) => size == DiscSize.SingleLayer ? 4_700_000_000L : 8_547_991_552L;

    public static DvdPlan Create(VideoInfo video, TelevisionSystem system, DiscSize size, int rotation)
    {
        if (!Enum.IsDefined(system) || !Enum.IsDefined(size) || rotation % 90 != 0)
            throw new ArgumentException("Choose PAL/NTSC, a supported disc capacity and quarter-turn rotation.");
        if (!double.IsFinite(video.Duration) || video.Duration <= 0 ||
            video.Width <= 0 || video.Height <= 0 || !double.IsFinite(video.PixelAspect) || video.PixelAspect <= 0)
            throw new ArgumentException("The video has no valid duration or dimensions.");
        long capacity = DiscCapacity(size);
        double available = capacity * .95 - 32 * 1024 * 1024;
        double budget = available * 8 / video.Duration / 1.04 - AudioBitrate;
        if (budget < MinimumBitrate)
            throw new InvalidOperationException(
                $"This {video.Duration / 60:F0}-minute video is too long for this disc at acceptable quality. " +
                "Choose a dual-layer DVD+R DL or split the video into shorter files. Nothing has been encoded.");
        int bitrate = (int)Math.Min(8_000_000, Math.Floor(budget / 1000) * 1000);
        return new(system, capacity, bitrate, video.DisplayAspect(rotation) >= 1.55,
            bitrate < 3_000_000 ? 352 : 720, system == TelevisionSystem.PAL ? 576 : 480);
    }

    public string Aspect => Wide ? "16:9" : "4:3";
    public string FrameRate => System == TelevisionSystem.PAL ? "25" : "30000/1001";
    public int Gop => System == TelevisionSystem.PAL ? 15 : 18;

    // FFmpeg autorotates from source metadata before this filter. Work in square pixels
    // to preserve display aspect, then convert to DVD's non-square pixel geometry.
    public string Filter(int rotation, bool preview = false)
    {
        string turn = (((rotation % 360) + 360) % 360) switch
        {
            0 => "",
            90 => ",transpose=clock",
            180 => ",hflip,vflip",
            270 => ",transpose=cclock",
            _ => throw new ArgumentException("Rotation must be a multiple of 90 degrees.")
        };
        int canvasWidth = Wide ? 1024 : 768;
        string sar = ((Wide ? 16.0 / 9 : 4.0 / 3) * Height / Width)
            .ToString("0.############", CultureInfo.InvariantCulture);
        string filter = $"scale=trunc(iw*sar/2)*2:ih,setsar=1{turn}," +
            $"scale={canvasWidth}:576:force_original_aspect_ratio=decrease:force_divisible_by=2," +
            $"pad={canvasWidth}:576:(ow-iw)/2:(oh-ih)/2:black,scale={Width}:{Height},setsar={sar}";
        return preview ? filter + $",scale={canvasWidth}:576,setsar=1" : filter;
    }
}
