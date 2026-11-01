namespace Compressr.Core;

public sealed record VideoClip(string Source, VideoInfo Info, int Rotation = 0);
public sealed record YouTubeInfo(string Title, double Duration);

public static class VideoPlaylist
{
    public static VideoInfo Combined(IReadOnlyList<VideoClip> clips)
    {
        if (clips.Count == 0) throw new ArgumentException("Add at least one video.");
        foreach (var clip in clips)
        {
            if (!double.IsFinite(clip.Info.Duration) || clip.Info.Duration <= 0)
                throw new ArgumentException("Every clip must have a finite positive duration.");
            _ = DvdPlan.Create(clip.Info with { Duration = 1 }, TelevisionSystem.PAL, DiscSize.SingleLayer, clip.Rotation);
        }
        double duration = clips.Sum(c => c.Info.Duration);
        if (!double.IsFinite(duration) || duration <= 0) throw new ArgumentException("Invalid combined video duration.");
        bool wide = clips.Count > 1 || clips.Any(c => c.Info.DisplayAspect(c.Rotation) >= 1.55);
        return new(duration, wide ? 1920 : 1440, 1080, 1, 0, true);
    }

    public static string FitEstimate(VideoInfo combined, TelevisionSystem system)
    {
        string Estimate(DiscSize size)
        {
            try
            {
                var plan = DvdPlan.Create(combined, system, size, 0);
                return $"fits at {plan.VideoBitrate / 1_000_000.0:F2} Mbps" +
                    (plan.Width == 352 ? " (half-D1)" : "");
            }
            catch (InvalidOperationException) { return "too long at the 2 Mbps quality floor"; }
        }
        return $"{combined.Duration / 60:F1} minutes total. Single-layer: {Estimate(DiscSize.SingleLayer)}. " +
            $"Dual-layer: {Estimate(DiscSize.DualLayer)}.";
    }

    public static string ConcatFilter(IReadOnlyList<VideoClip> clips, DvdPlan plan, bool audio)
    {
        _ = Combined(clips);
        var filters = new List<string>();
        for (int i = 0; i < clips.Count; i++)
        {
            var clip = clips[i];
            string duration = clip.Info.Duration.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
            filters.Add($"[{i}:v:0]{plan.Filter(clip.Rotation)},fps={plan.FrameRate},setpts=PTS-STARTPTS[v{i}]");
            if (audio)
            {
                filters.Add(clip.Info.HasAudio
                    ? $"[{i}:a:0]aresample=48000:async=1:first_pts=0,aformat=sample_fmts=fltp:channel_layouts=stereo,apad,atrim=duration={duration},asetpts=PTS-STARTPTS[a{i}]"
                    : $"anullsrc=r=48000:cl=stereo,atrim=duration={duration},asetpts=PTS-STARTPTS[a{i}]");
            }
        }
        string inputs = string.Concat(Enumerable.Range(0, clips.Count).Select(i => $"[v{i}]" + (audio ? $"[a{i}]" : "")));
        filters.Add($"{inputs}concat=n={clips.Count}:v=1:a={(audio ? 1 : 0)}[video]" + (audio ? "[audio]" : ""));
        return string.Join(";", filters);
    }
}
