using System.Globalization;
using System.Text.Json;
using Compressr.Core;

if (args.FirstOrDefault() == "--process-fixture")
{
    Environment.ExitCode = await ReliabilityTests.RunFixtureAsync(args[1..]);
    return;
}
if (args.FirstOrDefault() == "--image-context")
{
    await ReliabilityTests.RunImageContextAsync(args[1], args[2], new Progress<StageProgress>(), (condition, message) =>
    {
        if (!condition) throw new Exception("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    });
    return;
}

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

var listInfo = VideoPlaylist.Combined([new("one", landscape), new("two", phone, 90)]);
Check(listInfo.Duration == 7200 && listInfo.DisplayAspect(0) > 1.7, "combined duration and common wide canvas");
Check(VideoPlaylist.Combined([new("one", phone)]).DisplayAspect(0) < 1.55, "single portrait preserves existing 4:3 canvas choice");
Throws<ArgumentException>(() => VideoPlaylist.Combined([]), "empty playlist");
Throws<ArgumentException>(() => VideoPlaylist.Combined([new("bad", phone with { Duration = double.NaN })]), "invalid clip duration");
foreach (DiscSize size in Enum.GetValues<DiscSize>())
{
    double limit = (DvdPlan.DiscCapacity(size) * .95 - 32 * 1024 * 1024) * 8 /
        ((DvdPlan.MinimumBitrate + DvdPlan.AudioBitrate) * 1.04);
    var fit = VideoPlaylist.Combined([new("one", landscape with { Duration = limit / 2 - .1 }),
        new("two", phone with { Duration = limit / 2 })]);
    Check(DvdPlan.Create(fit, TelevisionSystem.PAL, size, 0).VideoBitrate >= DvdPlan.MinimumBitrate, "playlist just fits " + size);
    Throws<InvalidOperationException>(() => DvdPlan.Create(fit with { Duration = limit + .1 }, TelevisionSystem.PAL, size, 0), "playlist exceeds boundary " + size);
}
var concat = VideoPlaylist.ConcatFilter([new("one", landscape), new("two", phone, 90)], pal, true);
Check(concat.Contains("[v0][a0][v1][a1]concat=n=2:v=1:a=1"), "explicit clip ordering");
Check(concat.Contains("anullsrc=r=48000:cl=stereo") && concat.Contains("aresample=48000"), "normalizes mixed audio and silence");
Check(MediaTools.ValidateYouTube("https://www.youtube.com/watch?v=LjAJzcHZSq0&t=1082s").Fragment == "", "timestamp URL accepted without trimming");
var concert = landscape with { Duration = 10472 };
Check(DvdPlan.Create(concert, TelevisionSystem.PAL, DiscSize.SingleLayer, 0).VideoBitrate == 3_063_000, "concert metadata total single-layer estimate");
Check(DvdPlan.Create(concert, TelevisionSystem.PAL, DiscSize.DualLayer, 0).VideoBitrate == 5_748_000, "concert metadata total dual-layer estimate");
Console.WriteLine($"PASS: {assertions} unit/playlist assertions");
long singleCapacity = DvdPlan.DiscCapacity(DiscSize.SingleLayer) / 2048 * 2048;
PreparedDvd.CheckCapacity(singleCapacity, singleCapacity);
Check(true, "image fits exact media boundary");
Throws<InvalidOperationException>(() => PreparedDvd.CheckCapacity(singleCapacity + 2048, singleCapacity), "image exceeds media boundary");
Throws<InvalidDataException>(() => PreparedDvd.CheckCapacity(2049, singleCapacity), "partial image sector");
Throws<InvalidDataException>(() => PreparedDvd.CheckCapacity(0, singleCapacity), "empty image");
Throws<InvalidDataException>(() => PreparedDvd.CheckCapacity(DvdPlan.DiscCapacity(DiscSize.DualLayer) + 2048, long.MaxValue), "oversized image");
var attached = new DvdMedia(7, true, true, true, true, 2295104, 2295104);
Check(attached.Name == "DVD+RW" && attached.AvailableBytes == 4_700_372_992, "attached DVD+RW exact format/capacity");
attached.Check(singleCapacity);
Check(true, "single-layer image supported on attached media snapshot");
Throws<InvalidOperationException>(() => attached.Check(DvdPlan.DiscCapacity(DiscSize.DualLayer) / 2048 * 2048), "dual-layer image rejected on actual single-layer disc");
foreach (int type in new[] { 6, 7, 8, 9, 10, 11, 13 })
{
    (attached with { Type = type }).Check(2048);
    Check(true, "recordable DVD type supported " + type);
}
foreach (int type in new[] { 0, 1, 2, 3, 4, 5, 12, 14, 18, 19 })
    Throws<InvalidOperationException>(() => (attached with { Type = type }).Check(2048), "non-player DVD or non-DVD rejected " + type);
Throws<InvalidOperationException>(() => (attached with { PhysicallyBlank = false, HeuristicallyBlank = false }).Check(2048), "nonblank RW never erased");
Throws<InvalidOperationException>(() => (attached with { RecorderSupported = false }).Check(2048), "unsupported recorder");
Throws<InvalidOperationException>(() => (attached with { MediaSupported = false }).Check(2048), "unsupported current medium");
Throws<InvalidOperationException>(() => (attached with { FreeSectors = -1 }).Check(2048), "invalid free sectors");
Throws<InvalidOperationException>(() => (attached with { FreeSectors = attached.TotalSectors + 1 }).Check(2048), "inconsistent capacity");

foreach (int status in new[] { 0, 0x400, 0x800, 0x1000, 0x2000, 0x4000, 0x8000, 0x2002 })
{
    var blocked = attached with { CurrentStatus = status };
    Throws<InvalidOperationException>(() => blocked.Check(2048), $"blocking IMAPI state {status:X}");
    Check(!DiscReadiness.Assess(blocked, 2048).Ready, $"blocking state is never an automatic burn default {status:X}");
}
foreach (var invalidMedia in new[]
{
    attached with { Type = 0 }, attached with { Type = 5 }, attached with { Type = 4 },
    attached with { PhysicallyBlank = false, HeuristicallyBlank = false },
    attached with { RecorderSupported = false }, attached with { MediaSupported = false },
    attached with { FreeSectors = 0 }, attached with { FreeSectors = long.MaxValue },
    attached with { FreeSectors = attached.TotalSectors + 1 }
})
    Check(!DiscReadiness.Assess(invalidMedia, 2048).Ready, "invalid, missing or inaccessible media is not ready");
var ready = DiscReadiness.Assess(attached, singleCapacity);
var tooSmall = DiscReadiness.Assess(attached, DvdPlan.DiscCapacity(DiscSize.DualLayer) / 2048 * 2048);
Check(ready.Ready && !tooSmall.Ready && tooSmall.Detail.Contains("capacity"), "readiness uses required byte capacity");
Check(!DiscReadiness.NoWriter.Ready && !DiscReadiness.Failed("access denied").Ready &&
    DiscReadiness.Failed("access denied").Detail.Contains("access denied"), "no writer and failed detection are explicit");
var selection = new DvdOutputSelection();
Check(selection.Selected == OutputKind.Iso && !selection.IsManual, "safe initial file default");
selection.Apply(ready);
Check(selection.Selected == OutputKind.Burn, "ready blank DVD automatically selects burn");
foreach (var absent in new[] { DiscReadiness.NoWriter, tooSmall, DiscReadiness.Failed("device removed") })
{
    selection.Apply(absent);
    Check(selection.Selected == OutputKind.Iso, "automatic falls back to ISO before operation for unready media");
    selection.Apply(ready);
    Check(selection.Selected == OutputKind.Burn, "replacement ready media refreshes automatic choice");
}
foreach (OutputKind manual in Enum.GetValues<OutputKind>())
{
    selection.Choose(manual);
    foreach (var state in new[] { ready, tooSmall, DiscReadiness.NoWriter, DiscReadiness.Failed("access denied") })
    {
        selection.Apply(state);
        Check(selection.Selected == manual && selection.IsManual, "explicit output survives refresh/removal/failure: " + manual);
    }
}
selection.UseAutomatic(DiscReadiness.NoWriter);
Check(selection.Selected == OutputKind.Iso && !selection.IsManual, "user can resume automatic selection");
Throws<ArgumentOutOfRangeException>(() => selection.Choose((OutputKind)99), "invalid output selection");
Console.WriteLine($"PASS: {assertions} unit/media/output-selection assertions");
await DiscPreparationTests.RunAsync(Check);
Console.WriteLine($"PASS: {assertions} unit/media/output/preparation assertions (fake erasure only)");
await ReliabilityTests.RunProcessAsync(Check);
Console.WriteLine($"PASS: {assertions} focused assertions including tool process cleanup");

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
    byte[] navigationBytes = File.ReadAllBytes(Path.Combine(dvd, "VIDEO_TS", "VIDEO_TS.IFO"));
    Check(navigationBytes[35] == 0, test.Name + " no DVD region restrictions");
    int firstPlay = checked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(navigationBytes.AsSpan(132, 4)));
    int commands = firstPlay + System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(navigationBytes.AsSpan(firstPlay + 228, 2));
    Check(navigationBytes.AsSpan(commands + 8, 8).SequenceEqual(new byte[] { 0x30, 0x02, 0, 0, 0, 1, 0, 0 }),
        test.Name + " authored first-play command jumps to title 1");
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
// Distinct colors and durations prove clip order and completeness after real DVD authoring.
string red = Path.Combine(root, "red.mp4"), blue = Path.Combine(root, "blue-rotated.mp4"),
    blueBase = Path.Combine(root, "blue.mp4"), green = Path.Combine(root, "green.mp4");
await tools.RunAsync(tools.Ffmpeg, ["-y", "-f", "lavfi", "-i", "color=red:size=320x180:rate=24",
    "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=44100", "-t", "1.25", "-c:v", "mpeg4",
    "-c:a", "aac", "-ac", "1", red], token);
await tools.RunAsync(tools.Ffmpeg, ["-y", "-f", "lavfi", "-i", "color=blue:size=320x180:rate=30",
    "-t", "2", "-c:v", "mpeg4", blueBase], token);
await tools.RunAsync(tools.Ffmpeg, ["-y", "-display_rotation:v:0", "90", "-i", blueBase,
    "-c", "copy", blue], token);
await tools.RunAsync(tools.Ffmpeg, ["-y", "-f", "lavfi", "-i", "color=lime:size=320x240:rate=60",
    "-f", "lavfi", "-i", "sine=frequency=220:sample_rate=32000", "-vf", "setsar=2/1", "-t", "1.75",
    "-c:v", "mpeg4", "-c:a", "aac", green], token);
var ordered = new List<VideoClip>();
foreach (string file in new[] { red, blue, green }) ordered.Add(new(file, await tools.ProbeAsync(file, token)));
foreach (var test in new[] { (Name: "stitch-pal", System: TelevisionSystem.PAL, Reverse: false),
    (Name: "stitch-ntsc", System: TelevisionSystem.NTSC, Reverse: false),
    (Name: "stitch-reverse-pal", System: TelevisionSystem.PAL, Reverse: true) })
{
    var clips = test.Reverse ? ordered.AsEnumerable().Reverse().ToArray() : ordered.ToArray();
    var combined = VideoPlaylist.Combined(clips);
    var plan = DvdPlan.Create(combined, test.System, DiscSize.SingleLayer, 0);
    string work = Path.Combine(root, test.Name);
    string dvd = await new DvdPipeline(tools).AuthorPlaylistAsync(clips, plan, work, progress, token);
    await tools.CreateIsoAsync(dvd, Path.Combine(root, test.Name + ".iso"), plan, progress, token);
    string title = Path.Combine(work, "title.mpg");
    var resultInfo = await tools.ProbeAsync(title, token);
    Check(Math.Abs(resultInfo.Duration - combined.Duration) < .2, test.Name + " all clip durations retained");
    Check(resultInfo.Width == 720 && resultInfo.Height == plan.Height && resultInfo.HasAudio, test.Name + " normalized geometry/audio");
    double offset = 0;
    foreach (var clip in clips)
    {
        double seek = offset + clip.Info.Duration / 2;
        string frame = Path.Combine(work, $"order-{offset:F2}.rgb");
        await tools.RunAsync(tools.Ffmpeg, ["-y", "-ss", seek.ToString(CultureInfo.InvariantCulture),
            "-i", title, "-frames:v", "1", "-pix_fmt", "rgb24", "-f", "rawvideo", frame], token);
        byte[] pixels = File.ReadAllBytes(frame);
        int center = (plan.Height / 2 * plan.Width + plan.Width / 2) * 3;
        int channel = clip.Source == red ? 0 : clip.Source == green ? 1 : 2;
        Check(pixels[center + channel] > 180 && Enumerable.Range(0, 3).Where(c => c != channel).All(c => pixels[center + c] < 40),
            test.Name + " decoded clip order " + Path.GetFileName(clip.Source));
        if (clip.Source == blue)
        {
            int edge = (plan.Height / 2 * plan.Width + 4) * 3;
            Check(pixels.Skip(edge).Take(3).All(b => b < 20), test.Name + " rotated portrait padding");
            string audio = Path.Combine(work, "silent.pcm");
            await tools.RunAsync(tools.Ffmpeg, ["-y", "-ss", seek.ToString(CultureInfo.InvariantCulture),
                "-i", title, "-t", "0.2", "-vn", "-f", "s16le", "-acodec", "pcm_s16le", audio], token);
            Check(File.ReadAllBytes(audio).All(b => b == 0), test.Name + " silent middle clip has no neighbouring audio");
        }
        offset += clip.Info.Duration;
    }
    File.WriteAllText(Path.Combine(work, "ffprobe.json"),
        await tools.RunAsync(tools.Ffprobe, ["-v", "error", "-show_streams", "-show_format", "-of", "json", title], token));
    Console.WriteLine($"PASS: {test.Name}: clip order, complete duration, geometry and silence");
}
async Task RejectAsync<T>(string path, string message, CancellationToken cancellation = default) where T : Exception
{
    try { using var rejected = await PreparedDvd.OpenAsync(path, progress, cancellation); }
    catch (T) { Check(true, message); return; }
    throw new Exception("FAIL: accepted " + message);
}
foreach (string iso in Directory.GetFiles(root, "*.iso"))
{
    using var prepared = await PreparedDvd.OpenAsync(iso, progress, token);
    string work = Path.Combine(root, Path.GetFileNameWithoutExtension(iso));
    await prepared.VerifyDirectoryAsync(Path.Combine(work, "dvd", "VIDEO_TS"), progress, token);
    Check(prepared.Files.Count >= 5, "prepared image navigation and title files: " + iso);
    Throws<IOException>(() => { using var overwrite = new FileStream(iso, FileMode.Open, FileAccess.Write); }, "image locked against changes");
    string independent = Path.Combine(root, "readback", Path.GetFileNameWithoutExtension(iso), "VIDEO_TS");
    Directory.CreateDirectory(independent);
    foreach (string file in Directory.GetFiles(Path.Combine(work, "dvd", "VIDEO_TS")))
        File.Copy(file, Path.Combine(independent, Path.GetFileName(file)));
}
// Hide every source and original job before reopening saved images, as in a later app run.
foreach (string source in Directory.GetFiles(root, "*.mp4")) File.Move(source, source + ".unavailable");
foreach (string iso in Directory.GetFiles(root, "*.iso"))
{
    string work = Path.Combine(root, Path.GetFileNameWithoutExtension(iso));
    Directory.Move(work, work + ".unavailable");
    try
    {
        using var reopened = await PreparedDvd.OpenAsync(iso, progress, token);
        await reopened.VerifyDirectoryAsync(Path.Combine(root, "readback", Path.GetFileNameWithoutExtension(iso), "VIDEO_TS"), progress, token);
        Check(true, "saved ISO reusable without source/jobs: " + iso);
    }
    finally { Directory.Move(work + ".unavailable", work); }
}
foreach (string source in Directory.GetFiles(root, "*.mp4.unavailable"))
    File.Move(source, source[..^".unavailable".Length]);
string dataFolder = Path.Combine(root, "not-a-dvd");
Directory.CreateDirectory(dataFolder);
File.WriteAllText(Path.Combine(dataFolder, "data.txt"), "Not a DVD video title.");
string dataIso = Path.Combine(root, "not-a-dvd.test");
await tools.RunAsync(tools.Mkisofs, ["-udf", "-o", dataIso, dataFolder], token);
await RejectAsync<InvalidDataException>(dataIso, "real generic UDF data ISO");
File.Delete(dataIso);
string validIso = Path.Combine(root, "stitch-pal.iso");
using (var dvd = await PreparedDvd.OpenAsync(validIso, progress, token))
{
    string invalid = Path.Combine(root, "invalid-image.test");
    async Task CorruptAsync(long offset, byte[] bytes, string name)
    {
        File.Copy(validIso, invalid, true);
        using (var file = new FileStream(invalid, FileMode.Open, FileAccess.Write))
        { file.Position = offset; file.Write(bytes); }
        await RejectAsync<InvalidDataException>(invalid, name);
    }
    await CorruptAsync(16 * 2048 + 1, [0], "non-ISO file");
    await CorruptAsync(16 * 2048 + 80, [0, 0, 0, 0], "inconsistent volume size");
    await CorruptAsync(256 * 2048, [0, 0], "missing UDF anchor");
    await CorruptAsync(dvd.Files.Single(f => f.Name == "VIDEO_TS.IFO").Offset, [0], "invalid DVD navigation");
    await CorruptAsync(dvd.Files.Single(f => f.Name == "VIDEO_TS.BUP").Offset + 100, [255], "damaged navigation backup");
    await CorruptAsync(dvd.Files.Single(f => f.Name == "VTS_01_1.VOB").Offset, [255], "non-DVD program stream");
    await CorruptAsync(dvd.Files.Single(f => f.Name == "VTS_01_1.VOB").Offset + 2048, [255], "damaged later DVD sector");
    await CorruptAsync(dvd.Files.Single(f => f.Name == "VTS_01_0.IFO").Offset + 512, [0], "incompatible DVD video attributes");
    File.Copy(validIso, invalid, true);
    using (var file = new FileStream(invalid, FileMode.Open, FileAccess.Write)) file.SetLength(dvd.Length - 2048);
    await RejectAsync<InvalidDataException>(invalid, "sector-aligned truncation");
    using (var file = new FileStream(invalid, FileMode.Open, FileAccess.Write)) file.SetLength(2049);
    await RejectAsync<InvalidDataException>(invalid, "partial sector truncation");
    File.Delete(invalid);
    await RejectAsync<OperationCanceledException>(validIso, "inspection cancellation", new CancellationToken(true));
    string readback = Path.Combine(root, "readback", "stitch-pal", "VIDEO_TS");
    string damagedFile = Path.Combine(readback, "VTS_01_1.VOB");
    using (var file = new FileStream(damagedFile, FileMode.Open, FileAccess.Write)) file.WriteByte(255);
    try { await dvd.VerifyDirectoryAsync(readback, progress, token); throw new Exception("FAIL: damaged read-back accepted"); }
    catch (IOException) { Check(true, "read-back corruption explicitly rejected"); }
}
Console.WriteLine($"PASS: {assertions} total assertions; generated ISO/VIDEO_TS results in {root}");
await ReliabilityTests.RunImageContextAsync(Path.Combine(root, "landscape-pal.iso"),
    Path.Combine(root, "landscape-pal", "dvd", "VIDEO_TS"), progress, Check);
Console.WriteLine($"PASS: {assertions} total assertions including background image validation/read-back");
