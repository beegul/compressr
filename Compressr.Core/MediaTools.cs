using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace Compressr.Core;

public sealed record StageProgress(string Stage, double? Fraction, string Detail);

public sealed class MediaTools
{
    public string Ffmpeg { get; }
    public string Ffprobe { get; }
    public string YtDlp { get; }
    public string DvdAuthor { get; }
    public string Mkisofs { get; }

    public MediaTools(string directory)
    {
        Ffmpeg = Find(directory, "ffmpeg.exe");
        Ffprobe = Find(directory, "ffprobe.exe");
        YtDlp = Find(directory, "yt-dlp.exe");
        DvdAuthor = Find(directory, "dvdauthor.exe");
        Mkisofs = Find(directory, "mkisofs.exe");
    }

    private static string Find(string root, string name)
    {
        var locations = new[] { root, Path.Combine(root, "DVDStylerPortable", "App", "DVDStyler", "bin") }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        return locations.Select(p => Path.Combine(p, name)).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException($"Missing {name}. Run scripts\\Setup-Tools.ps1 or put the tool on PATH.", name);
    }

    public async Task<string> RunAsync(string tool, IEnumerable<string> args, CancellationToken cancellation,
        Action<string>? line = null, string? workingDirectory = null)
    {
        cancellation.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(tool)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        process.Start();
        void StopProcess()
        {
            try { if (!process.HasExited) process.Kill(true); }
            catch (InvalidOperationException) { /* Process exited between the check and kill. */ }
        }
        using var registration = cancellation.Register(StopProcess);
        var output = new System.Text.StringBuilder();
        var errors = new Queue<string>();
        Exception? callbackError = null;
        async Task ReadAsync(StreamReader reader, bool error)
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } text)
            {
                if (error) { errors.Enqueue(text); if (errors.Count > 80) errors.Dequeue(); }
                else output.AppendLine(text);
                if (line != null && Volatile.Read(ref callbackError) == null)
                {
                    try { line(text); }
                    catch (Exception ex)
                    {
                        if (Interlocked.CompareExchange(ref callbackError, ex, null) == null) StopProcess();
                    }
                }
            }
        }
        await Task.WhenAll(ReadAsync(process.StandardOutput, false), ReadAsync(process.StandardError, true),
            process.WaitForExitAsync(CancellationToken.None)).ConfigureAwait(false);
        if (callbackError != null) ExceptionDispatchInfo.Capture(callbackError).Throw();
        cancellation.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(tool)} failed ({process.ExitCode}).\n{string.Join("\n", errors)}");
        return output.ToString();
    }

    public async Task CreateIsoAsync(string dvd, string output, DvdPlan plan,
        IProgress<StageProgress> progress, CancellationToken token)
    {
        progress.Report(new("Image", null, "Creating DVD-Video UDF 1.02 / ISO9660 image…"));
        await RunAsync(Mkisofs, ["-dvd-video", "-udf", "-V", "COMPRESSR", "-o", output, dvd], token);
        if (!File.Exists(output) || new FileInfo(output).Length == 0 || new FileInfo(output).Length > plan.Capacity)
            throw new InvalidOperationException("The DVD image is empty or exceeds the selected disc capacity.");
    }

    public async Task<VideoInfo> ProbeAsync(string source, CancellationToken token)
    {
        string json = await RunAsync(Ffprobe, ["-v", "error", "-show_streams", "-show_format", "-of", "json", source], token);
        using var doc = JsonDocument.Parse(json);
        var streams = doc.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.FirstOrDefault(s => s.GetProperty("codec_type").GetString() == "video");
        if (video.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException("No video stream was found.");
        double duration = double.Parse(doc.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        double sar = 1;
        if (video.TryGetProperty("sample_aspect_ratio", out var aspect))
        {
            var pair = (aspect.GetString() ?? "").Split(':');
            if (pair.Length == 2 && double.TryParse(pair[0], CultureInfo.InvariantCulture, out double a) &&
                double.TryParse(pair[1], CultureInfo.InvariantCulture, out double b) && a > 0 && b > 0) sar = a / b;
        }
        int rotation = 0;
        if (video.TryGetProperty("side_data_list", out var side))
            foreach (var item in side.EnumerateArray())
                if (item.TryGetProperty("rotation", out var r)) rotation = (int)Math.Round(r.GetDouble());
        return new(duration, video.GetProperty("width").GetInt32(), video.GetProperty("height").GetInt32(), sar,
            rotation, streams.Any(s => s.GetProperty("codec_type").GetString() == "audio"));
    }

    public static Uri ValidateYouTube(string input)
    {
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !(uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Paste an HTTPS YouTube video link.");
        return uri;
    }

    public async Task<YouTubeInfo> YouTubeMetadataAsync(string input, CancellationToken token)
    {
        var uri = ValidateYouTube(input);
        var args = new List<string> { "--no-playlist", "--skip-download", "--dump-json" };
        string deno = Path.Combine(Path.GetDirectoryName(YtDlp)!, "deno.exe");
        if (File.Exists(deno)) args.AddRange(["--js-runtimes", "deno:" + deno]);
        args.AddRange(["--", uri.AbsoluteUri]);
        using var doc = JsonDocument.Parse(await RunAsync(YtDlp, args, token));
        double duration = doc.RootElement.GetProperty("duration").GetDouble();
        if (!double.IsFinite(duration) || duration <= 0)
            throw new InvalidOperationException("YouTube did not provide a finite duration; live streams are not supported.");
        return new(doc.RootElement.GetProperty("title").GetString() ?? "YouTube video", duration);
    }

    public async Task<string> DownloadAsync(string url, string directory, IProgress<StageProgress> progress, CancellationToken token)
    {
        var uri = ValidateYouTube(url);
        Directory.CreateDirectory(directory);
        progress.Report(new("Download", null, "Downloading one video with yt-dlp…"));
        var args = new List<string> { "--no-playlist", "--newline", "--no-progress", "--ffmpeg-location",
            Path.GetDirectoryName(Ffmpeg)!, "-f", "bv*[height<=1080]+ba/b[height<=1080]/b",
            "--merge-output-format", "mkv", "-o", Path.Combine(directory, "source.%(ext)s") };
        string deno = Path.Combine(Path.GetDirectoryName(YtDlp)!, "deno.exe");
        if (File.Exists(deno)) args.AddRange(["--js-runtimes", "deno:" + deno]);
        args.AddRange(["--", uri.AbsoluteUri]);
        await RunAsync(YtDlp, args, token);
        var files = Directory.GetFiles(directory, "source.*").Where(p => !p.EndsWith(".part")).ToArray();
        if (files.Length != 1) throw new InvalidOperationException("yt-dlp did not produce a single complete video.");
        return files[0];
    }
}
