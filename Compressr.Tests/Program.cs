using System.Globalization;
using System.Text.Json;
using Compressr.Core;

int assertions = 0;
void Check(bool condition, string message)
{
    assertions++;
    if (!condition) throw new Exception("FAIL: " + message);
}
void Throws<T>(Action action, string name) where T : Exception
{
    try { action(); }
    catch (T) { assertions++; return; }
    throw new Exception("FAIL: expected " + typeof(T).Name + ": " + name);
}
var landscape = new VideoInfo(3600, 1920, 1080, 1, 0, true);
var phone = new VideoInfo(3600, 1920, 1080, 1, 90, false);
var pal = DvdPlan.Create(landscape, TelevisionSystem.PAL, DiscSize.SingleLayer, 0);
Check(pal.Wide && pal.Width == 720 && pal.Height == 576, "PAL landscape dimensions");
Check(DvdPlan.Create(phone, TelevisionSystem.NTSC, DiscSize.SingleLayer, 0).Wide == false, "rotation metadata chooses portrait DAR");
Check(DvdPlan.Create(phone, TelevisionSystem.PAL, DiscSize.SingleLayer, 90).Wide, "manual rotation is additive to metadata");
Check(DvdPlan.Create(landscape with { PixelAspect = .5 }, TelevisionSystem.PAL, DiscSize.SingleLayer, 0).Wide == false, "source non-square pixels");
Check(pal.VideoBitrate <= 8_000_000 && pal.VideoBitrate >= DvdPlan.MinimumBitrate, "legal bitrate");
Check(((pal.VideoBitrate + DvdPlan.AudioBitrate) * landscape.Duration * 1.04 / 8 + 32 * 1024 * 1024) <= pal.Capacity * .95, "mux and author overhead budget");
Check(pal.Filter(0).Contains("force_original_aspect_ratio=decrease") && pal.Filter(0).Contains("pad=1024:576"), "letterbox without stretch/crop");
Check(pal.Filter(90).Contains("transpose=clock") && pal.Filter(270).Contains("transpose=cclock") &&
    pal.Filter(180).Contains("hflip,vflip"), "manual rotation filters");
Check(pal.Filter(0, true).EndsWith("scale=1024:576,setsar=1"), "preview uses square display pixels");
Throws<ArgumentException>(() => pal.Filter(45), "non-quarter-turn override");
Throws<ArgumentException>(() => DvdPlan.Create(landscape with { Duration = double.NaN }, TelevisionSystem.PAL, DiscSize.SingleLayer, 0), "NaN duration");
Throws<ArgumentException>(() => DvdPlan.Create(landscape with { Duration = 0 }, TelevisionSystem.PAL, DiscSize.SingleLayer, 0), "zero duration");
Throws<InvalidOperationException>(() => DvdPlan.Create(landscape with { Duration = 18000 }, TelevisionSystem.PAL, DiscSize.SingleLayer, 0), "too long");
foreach (DiscSize size in Enum.GetValues<DiscSize>())
{
    double boundary = (DvdPlan.DiscCapacity(size) * .95 - 32 * 1024 * 1024) * 8 /
        ((DvdPlan.MinimumBitrate + DvdPlan.AudioBitrate) * 1.04);
    Check(DvdPlan.Create(landscape with { Duration = boundary - .1 }, TelevisionSystem.PAL, size, 0).VideoBitrate >= DvdPlan.MinimumBitrate, "just fits " + size);
    Throws<InvalidOperationException>(() => DvdPlan.Create(landscape with { Duration = boundary + .1 }, TelevisionSystem.PAL, size, 0), "over boundary " + size);
    var low = DvdPlan.Create(landscape with { Duration = boundary - 1 }, TelevisionSystem.NTSC, size, 0);
    Check(low.Width == 352 && low.Height == 480, "half-D1 at safe low bitrate");
}
Check(DvdPlan.Create(landscape with { Duration = 14000 }, TelevisionSystem.PAL, DiscSize.DualLayer, 0).Capacity > pal.Capacity, "dual layer extends fit");
MediaTools.ValidateYouTube("https://www.youtube.com/watch?v=abc");
MediaTools.ValidateYouTube("https://youtu.be/abc");
Throws<ArgumentException>(() => MediaTools.ValidateYouTube("https://youtube.com.evil.example/watch?v=x"), "lookalike host");
Throws<ArgumentException>(() => MediaTools.ValidateYouTube("http://youtube.com/watch?v=x"), "insecure URL");
Throws<ArgumentException>(() => MediaTools.ValidateYouTube("file:///C:/private"), "non-YouTube URL");
Console.WriteLine($"PASS: {assertions} unit assertions");

if (!args.Contains("--integration")) return;
string root = Path.GetFullPath(args.SkipWhile(a => a != "--output").Skip(1).FirstOrDefault() ?? "artifacts");
Directory.CreateDirectory(root);
var tools = new MediaTools(Environment.GetEnvironmentVariable("COMPRESSR_TOOLS") ?? Path.GetFullPath("tools"));
var progress = new Progress<StageProgress>(p => { if (p.Fraction == null) Console.WriteLine($"{p.Stage}: {p.Detail}"); });
var token = CancellationToken.None;
string original = Path.Combine(root, "landscape.mp4");
await tools.RunAsync(tools.Ffmpeg, ["-y", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25",
    "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "3",
    "-c:v", "mpeg4", "-q:v", "3", "-c:a", "aac", original], token);
string silent = Path.Combine(root, "phone-rotation.mp4");
await tools.RunAsync(tools.Ffmpeg, ["-y", "-display_rotation:v:0", "90", "-i", original,
    "-map", "0:v:0", "-c", "copy", silent], token);
var phoneInfo = await tools.ProbeAsync(silent, token);
Check(Math.Abs(phoneInfo.Rotation) == 90 && !phoneInfo.HasAudio, "generated silent phone retains rotation metadata");
foreach (var test in new[]
{
    (Name: "landscape-pal", Source: original, System: TelevisionSystem.PAL, Rotation: 0),
    (Name: "portrait-pal", Source: silent, System: TelevisionSystem.PAL, Rotation: 0),
    (Name: "portrait-ntsc", Source: silent, System: TelevisionSystem.NTSC, Rotation: 0),
    (Name: "manual-ntsc", Source: silent, System: TelevisionSystem.NTSC, Rotation: 90),
    (Name: "half-d1-ntsc", Source: original, System: TelevisionSystem.NTSC, Rotation: 0)
})
{
    var info = await tools.ProbeAsync(test.Source, token);
    var plan = DvdPlan.Create(test.Name.StartsWith("half-d1") ? info with { Duration = 12000 } : info,
        test.System, DiscSize.SingleLayer, test.Rotation);
    string work = Path.Combine(root, test.Name);
    string dvd = await new DvdPipeline(tools).AuthorAsync(test.Source, info, plan, test.Rotation, work, progress, token);
    string iso = Path.Combine(root, test.Name + ".iso");
    await tools.CreateIsoAsync(dvd, iso, plan, progress, token);
    string vob = Path.Combine(dvd, "VIDEO_TS", "VTS_01_1.VOB");
    string result = await tools.RunAsync(tools.Ffprobe, ["-v", "error", "-show_streams", "-of", "json", vob], token);
    File.WriteAllText(Path.Combine(work, "ffprobe.json"), result);
    using var parsed = JsonDocument.Parse(result);
    var streams = parsed.RootElement.GetProperty("streams").EnumerateArray().ToArray();
    var v = streams.First(s => s.GetProperty("codec_type").GetString() == "video");
    var a = streams.First(s => s.GetProperty("codec_type").GetString() == "audio");
    Check(v.GetProperty("codec_name").GetString() == "mpeg2video", test.Name + " MPEG-2");
    Check(v.GetProperty("width").GetInt32() == plan.Width && v.GetProperty("height").GetInt32() == plan.Height, test.Name + " DVD geometry");
    Check(v.GetProperty("display_aspect_ratio").GetString() == plan.Aspect, test.Name + " DVD DAR");
    Check(v.GetProperty("r_frame_rate").GetString() == plan.FrameRate || (plan.System == TelevisionSystem.PAL &&
        v.GetProperty("r_frame_rate").GetString() == "25/1"), test.Name + " DVD frame rate");
    Check(a.GetProperty("codec_name").GetString() == "ac3" && a.GetProperty("sample_rate").GetString() == "48000", test.Name + " audio, including silence");
    Check(new FileInfo(iso).Length < plan.Capacity, test.Name + " ISO capacity");
    // Inspect black side bars in decoded output, not merely the filter string.
    string raw = Path.Combine(work, "frame.rgb");
    await tools.RunAsync(tools.Ffmpeg, ["-y", "-i", vob, "-vf", "scale=768:576,setsar=1",
        "-frames:v", "1", "-pix_fmt", "rgb24", "-f", "rawvideo", raw], token);
    var pixels = File.ReadAllBytes(raw);
    int left = (288 * 768 + 8) * 3;
    int center = (288 * 768 + 384) * 3;
    if (test.Name.StartsWith("portrait"))
        Check(pixels[left] < 20 && pixels[left + 1] < 20 && pixels[left + 2] < 20 &&
            pixels.Skip(center).Take(3).Any(b => b > 30), test.Name + " black bars and visible centre");
    Console.WriteLine($"PASS: {test.Name}: {iso}");
}
// A compact, genuine long-duration file: sparse low-frame-rate MPEG-4, not a huge test fixture.
string longClip = Path.Combine(root, "too-long.mp4");
await tools.RunAsync(tools.Ffmpeg, ["-y", "-f", "lavfi", "-i", "color=black:size=32x32:rate=1/600",
    "-t", "18000", "-c:v", "mpeg4", "-q:v", "31", longClip], token);
var longInfo = await tools.ProbeAsync(longClip, token);
Throws<InvalidOperationException>(() => DvdPlan.Create(longInfo, TelevisionSystem.PAL, DiscSize.SingleLayer, 0), "real long clip rejected before encoding");
if (args.Contains("--youtube"))
{
    string url = args.SkipWhile(a => a != "--youtube").Skip(1).First();
    string downloaded = await tools.DownloadAsync(url, Path.Combine(root, "youtube-" + Guid.NewGuid().ToString("N")), progress, token);
    Check((await tools.ProbeAsync(downloaded, token)).Duration > 0, "YouTube download probes");
}
Console.WriteLine($"PASS: {assertions} total assertions; generated ISO/VIDEO_TS results in {root}");
