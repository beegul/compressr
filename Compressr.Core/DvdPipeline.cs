using System.Globalization;
using System.Xml.Linq;

namespace Compressr.Core;

public sealed class DvdPipeline(MediaTools tools)
{
    public async Task<string> AuthorAsync(string source, VideoInfo video, DvdPlan plan, int rotation,
        string work, IProgress<StageProgress> progress, CancellationToken token)
    {
        Directory.CreateDirectory(work);
        string mpg = Path.Combine(work, "title.mpg");
        string passLog = Path.Combine(work, "encode");
        string duration = video.Duration.ToString("0.######", CultureInfo.InvariantCulture);
        string[] videoArgs = ["-map", "0:v:0", "-vf", plan.Filter(rotation), "-c:v", "mpeg2video",
            "-pix_fmt", "yuv420p", "-r", plan.FrameRate, "-g", plan.Gop.ToString(CultureInfo.InvariantCulture),
            "-b:v", plan.VideoBitrate.ToString(CultureInfo.InvariantCulture), "-maxrate:v", "8000000",
            "-bufsize:v", "1835008", "-flags", "+cgop", "-sc_threshold", "1000000000",
            "-aspect", plan.Aspect, "-passlogfile", passLog, "-t", duration];
        for (int pass = 1; pass <= 2; pass++)
        {
            int currentPass = pass;
            progress.Report(new("Convert", 0, $"Encoding pass {pass}/2…"));
            var args = new List<string> { "-y", "-nostdin", "-i", source };
            if (pass == 2 && !video.HasAudio) args.AddRange(["-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo"]);
            args.AddRange(videoArgs);
            args.AddRange(["-pass", pass.ToString(CultureInfo.InvariantCulture), "-progress", "pipe:1", "-nostats"]);
            if (pass == 1) args.AddRange(["-an", "-f", "null", "NUL"]);
            else args.AddRange(["-map", video.HasAudio ? "0:a:0" : "1:a:0", "-af", "apad", "-c:a", "ac3",
                "-b:a", "192k", "-ar", "48000", "-ac", "2", "-f", "dvd", "-muxrate", "0", "-packetsize", "2048", mpg]);
            await tools.RunAsync(tools.Ffmpeg, args, token, line =>
            {
                if (line.StartsWith("out_time_us=") && double.TryParse(line[12..], CultureInfo.InvariantCulture, out double us))
                    progress.Report(new("Convert", Math.Clamp(((currentPass - 1) + us / 1_000_000 / video.Duration) / 2, 0, 1),
                        $"Encoding pass {currentPass}/2"));
            });
        }
        progress.Report(new("Author", null, "Creating autoplay DVD-Video…"));
        string dvd = Path.Combine(work, "dvd");
        string xml = Path.Combine(work, "dvd.xml");
        var videoElement = new XElement("video", new XAttribute("format", plan.System.ToString().ToLowerInvariant()),
            new XAttribute("aspect", plan.Aspect));
        if (plan.Wide) videoElement.Add(new XAttribute("widescreen", "nopanscan"));
        new XDocument(new XElement("dvdauthor", new XAttribute("dest", dvd),
            new XElement("vmgm", new XElement("fpc", "jump title 1;"),
                new XElement("menus", new XElement("video",
                    new XAttribute("format", plan.System.ToString().ToLowerInvariant())))),
            new XElement("titleset", new XElement("titles", videoElement,
                new XElement("audio", new XAttribute("format", "ac3"), new XAttribute("lang", "en")),
                new XElement("pgc", new XElement("vob", new XAttribute("file", mpg)),
                    new XElement("post", "exit;")))))).Save(xml);
        await tools.RunAsync(tools.DvdAuthor, ["-x", xml], token);
        string videoTs = Path.Combine(dvd, "VIDEO_TS");
        if (!File.Exists(Path.Combine(videoTs, "VIDEO_TS.IFO")) ||
            !Directory.GetFiles(videoTs, "VTS_*.VOB").Any())
            throw new InvalidOperationException("Authoring did not produce a valid VIDEO_TS tree.");
        long size = Directory.GetFiles(dvd, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);
        if (size > plan.Capacity * .98) throw new InvalidOperationException("The authored DVD exceeds the safe disc budget. No disc was written.");
        Directory.CreateDirectory(Path.Combine(dvd, "AUDIO_TS"));
        return dvd;
    }
}
