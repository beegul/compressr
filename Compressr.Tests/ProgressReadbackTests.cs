using System.Security.Cryptography;
using System.Net;
using System.Net.Sockets;
using Compressr.Core;

internal static class ProgressReadbackTests
{
    public static async Task RunDownloadIntegrationAsync(MediaTools tools, string source, string output, Action<bool, string> check)
    {
        // Loopback serves only our generated clip; no YouTube or user media is downloaded.
        var portFinder = new TcpListener(IPAddress.Loopback, 0);
        portFinder.Start();
        int port = ((IPEndPoint)portFinder.LocalEndpoint).Port;
        portFinder.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var stop = new CancellationTokenSource();
        byte[] data = await File.ReadAllBytesAsync(source);
        var server = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var request = await listener.GetContextAsync();
                    request.Response.ContentType = "video/mp4";
                    request.Response.ContentLength64 = data.Length;
                    try
                    {
                        if (request.Request.HttpMethod != "HEAD")
                            for (int offset = 0; offset < data.Length; offset += 4096)
                            {
                                await request.Response.OutputStream.WriteAsync(data.AsMemory(offset, Math.Min(4096, data.Length - offset)), stop.Token);
                                await Task.Delay(15, stop.Token);
                            }
                    }
                    catch (HttpListenerException ex) when (ex.NativeErrorCode is 64 or 1229)
                    { Console.WriteLine($"Loopback extractor closed its probe connection (Windows {ex.NativeErrorCode}); awaiting its actual download request."); }
                    finally { request.Response.Close(); }
                }
            }
            catch (Exception ex) when (stop.IsCancellationRequested && ex is HttpListenerException or ObjectDisposedException or OperationCanceledException) { }
        });
        var updates = new System.Collections.Concurrent.ConcurrentQueue<StageProgress>();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await tools.RunAsync(tools.YtDlp, ["--no-playlist", "--newline", "--progress", "--no-colors", "--progress-delta", "0.5",
                "--progress-template", "download:" + DownloadProgress.DownloadPrefix + "%(progress)j",
                "--progress-template", "postprocess:" + DownloadProgress.ProcessPrefix + "%(progress)j",
                "-o", output, "--", $"http://127.0.0.1:{port}/generated.mp4"], timeout.Token, line =>
            {
                if (DownloadProgress.Parse(line, "Video 1 of 1 · Generated loopback clip") is { } value) updates.Enqueue(value);
            });
            check(updates.Any(v => v.Stage == "Download" && v.Fraction is > 0 and < 1) &&
                updates.Any(v => v.Stage == "Download" && v.Fraction == 1), "bundled yt-dlp emits real structured incremental and final download progress");
            check(data.SequenceEqual(await File.ReadAllBytesAsync(output)), "progress-enabled bundled downloader saves generated clip bytes exactly");
            File.WriteAllLines(output + ".progress.txt", updates.Select(v => $"{v.Stage}: {v.Fraction} {v.Detail}"));
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
            await server;
        }
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        const string clip = "Video 2 of 3 · Generated clip";
        var update = DownloadProgress.Parse(DownloadProgress.DownloadPrefix +
            """{"status":"downloading","downloaded_bytes":25000000,"total_bytes":100000000,"speed":2000000,"eta":38}""", clip)!;
        check(update.Fraction == .25 && update.Detail.Contains(clip) && update.Detail.Contains("25.0%") &&
            update.Detail.Contains("25.0 MB / 100.0 MB") && update.Detail.Contains("2.0 MB/s") &&
            update.Detail.Contains("ETA 0:38"), "real progress fields render clip, percent, bytes, speed and ETA");
        update = DownloadProgress.Parse(DownloadProgress.DownloadPrefix +
            """{"status":"downloading","downloaded_bytes":10,"total_bytes":null,"speed":null,"eta":null}""", clip)!;
        check(update.Fraction == null && update.Detail.Contains("unknown") && update.Detail.Contains("ETA unavailable"),
            "unknown total/speed/ETA remain honestly indeterminate");
        update = DownloadProgress.Parse(DownloadProgress.DownloadPrefix +
            """{"status":"downloading","downloaded_bytes":50,"total_bytes_estimate":100}""", clip)!;
        check(update.Fraction == .5 && update.Detail.Contains("~"), "estimated total is visibly approximate");
        update = DownloadProgress.Parse(DownloadProgress.DownloadPrefix +
            """{"status":"finished","downloaded_bytes":100,"total_bytes":100}""", clip)!;
        check(update.Fraction == 1 && update.Detail.Contains("next stream or merge"), "completed stream is not whole-clip completion");
        update = DownloadProgress.Parse(DownloadProgress.DownloadPrefix +
            """{"status":"downloading","downloaded_bytes":1,"total_bytes":100}""", clip)!;
        check(update.Fraction == .01 && update.Detail.Contains("Current stream"), "next audio stream can reset percent without implying whole-job regression");
        check(DownloadProgress.Parse("[Merger] Merging formats into source.mkv", clip) is { Stage: "Merge", Fraction: null },
            "merge stage is separate and never assigned fake percent");
        check(DownloadProgress.Parse(DownloadProgress.ProcessPrefix + """{"status":"processing"}""", clip) is { Stage: "Merge", Fraction: null },
            "structured postprocessing is indeterminate");
        check(DownloadProgress.Parse("[youtube] Downloading webpage", clip) == null, "extractor diagnostics are not fake download updates");
        update = DownloadProgress.Parse(DownloadProgress.DownloadPrefix +
            """{"downloaded_bytes":-1,"total_bytes":0,"speed":"NA","eta":-1}""", clip)!;
        check(update.Fraction == null && update.Detail.Contains("ETA unavailable") && !update.Detail.Contains("/s"),
            "missing/non-numeric/invalid optional fields do not break progress");

        byte[] data = new byte[2 * 1024 * 1024];
        Random.Shared.NextBytes(data);
        var file = new DvdImageFile("VTS_01_1.VOB", 0, data.Length, SHA256.HashData(data));
        var reports = new Reports();
        int opens = 0;
        MemoryStream Open(string path) { opens++; return new(data, false); }
        await DvdReadback.VerifyAsync([file], "test-VIDEO_TS", reports, CancellationToken.None, Open);
        check(opens == 1 && reports.Values.Last().Fraction == 1 && reports.Values.Count >= 3,
            "readback reports actual byte progress inside large VOB and hashes every byte");
        check(reports.Values.Zip(reports.Values.Skip(1)).All(p => p.First.Fraction <= p.Second.Fraction),
            "readback byte progress is monotonic");
        var failure = new FaultyReadStream(data);
        opens = 0;
        try
        {
            await DvdReadback.VerifyAsync([file], "test-VIDEO_TS", new Reports(), CancellationToken.None, _ => { opens++; return failure; });
            check(false, "CRC must fail verification");
        }
        catch (DvdReadbackException ex)
        {
            check(ex.ReadFailed && ex.WindowsError == 23 && ex.BytesRead == 1024 * 1024 &&
                ex.FilePath.EndsWith(file.Name) && ex.Message.Contains("CRC") && ex.Message.Contains("NOT verified") &&
                ex.InnerException?.HResult == unchecked((int)0x80070017), "CRC preserves file/byte offset/native cause and explicit not-verified state");
        }
        check(opens == 1 && failure.Disposed, "CRC never blindly retries and always releases read handle");
        try
        {
            await DvdReadback.VerifyAsync([file with { Hash = new byte[32] }], "test-VIDEO_TS", new Reports(), CancellationToken.None, Open);
            check(false, "mismatched bytes must fail");
        }
        catch (DvdReadbackException ex)
        { check(!ex.ReadFailed && ex.BytesRead == data.Length && ex.Message.Contains("SHA-256"), "hash mismatch differs from device read failure"); }
        try
        {
            await DvdReadback.VerifyAsync([file], "test-VIDEO_TS", new Reports(), CancellationToken.None, _ => new MemoryStream(new byte[5]));
            check(false, "wrong length must fail");
        }
        catch (DvdReadbackException ex)
        { check(!ex.ReadFailed && ex.BytesRead == 0 && ex.Message.Contains("length"), "mounted-file length mismatch diagnosed without reading wrong bytes"); }
        try
        {
            await DvdReadback.VerifyAsync([file], "test-VIDEO_TS", new Reports(), CancellationToken.None,
                _ => throw new FileNotFoundException("No mounted file"));
            check(false, "missing file must fail");
        }
        catch (DvdReadbackException ex)
        { check(ex.ReadFailed && ex.Message.Contains("No mounted file"), "missing mounted file includes actionable read diagnostics"); }
        opens = 0;
        try
        {
            await DvdReadback.VerifyAsync([file], "test-VIDEO_TS", new Reports(), new CancellationToken(true), Open);
            check(false, "cancelled readback must fail");
        }
        catch (OperationCanceledException) { check(opens == 0, "pre-cancelled readback does not open media"); }
    }

    private sealed class Reports : IProgress<StageProgress>
    {
        public List<StageProgress> Values { get; } = [];
        public void Report(StageProgress value) => Values.Add(value);
    }

    private sealed class FaultyReadStream(byte[] data) : MemoryStream(data, false)
    {
        public bool Disposed { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (Position >= 1024 * 1024) throw new IOException("Data error (cyclic redundancy check).", unchecked((int)0x80070017));
            return base.ReadAsync(buffer, token);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
