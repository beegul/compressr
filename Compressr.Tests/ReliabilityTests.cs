using System.Diagnostics;
using System.Reflection;
using Compressr.Core;

internal static class ReliabilityTests
{
    public static async Task RunProcessAsync(Action<bool, string> check)
    {
        string state = Path.Combine(Path.GetTempPath(), "compressr-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        try
        {
            foreach (string tool in new[] { "ffmpeg.exe", "ffprobe.exe", "yt-dlp.exe", "dvdauthor.exe", "mkisofs.exe" })
                File.WriteAllText(Path.Combine(state, tool), "");
            var tools = new MediaTools(state);
            try
            {
                await tools.RunAsync(Path.Combine(state, "must-not-start.exe"), [], new CancellationToken(true));
                check(false, "pre-cancel must fail before launching a tool");
            }
            catch (OperationCanceledException) { check(true, "pre-cancel does not launch a tool"); }

            var fixture = ChildArguments("flood");
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(4)))
            {
                var elapsed = Stopwatch.StartNew();
                try
                {
                    await tools.RunAsync(fixture.Tool, fixture.Arguments, cancellation.Token,
                        _ => throw new InvalidDataException("Progress consumer failed"));
                    check(false, "callback failure must fail the operation");
                }
                catch (InvalidDataException ex)
                {
                    Console.WriteLine($"Progress callback failure cleanup: {elapsed.Elapsed.TotalMilliseconds:F0} ms");
                    check(ex.Message == "Progress consumer failed" && elapsed.Elapsed < TimeSpan.FromSeconds(3),
                        "callback failure kills the blocked tool promptly and preserves the original error");
                }
            }
            fixture = ChildArguments("tree", Path.Combine(state, "started"));
            using (var cancellation = new CancellationTokenSource())
            {
                var task = tools.RunAsync(fixture.Tool, fixture.Arguments, cancellation.Token);
                string marker = Path.Combine(state, "started");
                var deadline = Stopwatch.StartNew();
                while (!File.Exists(marker) && !task.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(5))
                    await Task.Delay(10);
                check(File.Exists(marker), "cancellation fixture really started");
                int pid = int.Parse(await File.ReadAllTextAsync(marker));
                int parentPid = int.Parse(await File.ReadAllTextAsync(marker + ".parent"));
                cancellation.Cancel();
                try { await task; check(false, "running tool cancellation"); }
                catch (OperationCanceledException) { check(true, "running tool reports cancellation"); }
                foreach (int stopped in new[] { parentPid, pid })
                {
                    bool exited;
                    try { using var child = Process.GetProcessById(stopped); exited = child.HasExited; }
                    catch (ArgumentException) { exited = true; }
                    check(exited, "cancelled operation leaves no running tool or descendant");
                }
            }
            fixture = ChildArguments("failure");
            try { await tools.RunAsync(fixture.Tool, fixture.Arguments, CancellationToken.None); check(false, "tool failure"); }
            catch (InvalidOperationException ex)
            { check(ex.Message.Contains("fixture diagnostic") && ex.Message.Contains("(17)"), "tool exit diagnostics preserved"); }
            fixture = ChildArguments("download-progress");
            var reports = new System.Collections.Concurrent.ConcurrentQueue<StageProgress>();
            await tools.RunAsync(fixture.Tool, fixture.Arguments, CancellationToken.None, text =>
            {
                if (DownloadProgress.Parse(text, "Video 1 of 2 · Fixture") is { } value) reports.Enqueue(value);
            });
            check(reports.Any(p => p.Stage == "Download" && p.Fraction == .5) && reports.Any(p => p.Stage == "Merge"),
                "stdout/stderr process pipeline delivers structured download and merge updates");
        }
        finally { Directory.Delete(state, true); }
    }

    private static (string Tool, string[] Arguments) ChildArguments(params string[] args)
    {
        string tool = Environment.ProcessPath ?? throw new InvalidOperationException("No test process executable.");
        string[] prefix = Path.GetFileNameWithoutExtension(tool).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? [Assembly.GetExecutingAssembly().Location] : [];
        return (tool, [.. prefix, "--process-fixture", .. args]);
    }

    public static async Task RunImageContextAsync(string iso, string videoTs, IProgress<StageProgress> progress,
        Action<bool, string> check)
    {
        var context = new TrackingContext();
        Task<PreparedDvd> opening;
        var original = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            opening = PreparedDvd.OpenAsync(iso, progress, CancellationToken.None);
        }
        finally { SynchronizationContext.SetSynchronizationContext(original); }
        using var dvd = await opening;
        Console.WriteLine($"ISO validation caller-context posts: {context.Posts}");
        check(context.Posts == 0, "ISO validation does not marshal hashing or sector scans onto the UI context");
        Task verifying;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            verifying = dvd.VerifyDirectoryAsync(videoTs, progress, CancellationToken.None);
        }
        finally { SynchronizationContext.SetSynchronizationContext(original); }
        await verifying;
        Console.WriteLine($"ISO validation/read-back caller-context posts: {context.Posts}");
        check(context.Posts == 0, "DVD read-back does not marshal hashing onto the UI context");
    }

    private sealed class TrackingContext : SynchronizationContext
    {
        private int posts;
        public int Posts => Volatile.Read(ref posts);
        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref posts);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
    }

    public static async Task<int> RunFixtureAsync(string[] args)
    {
        switch (args[0])
        {
            case "flood":
                while (true) Console.WriteLine(new string('x', 8192));
            case "wait":
                File.WriteAllText(args[1] + ".tmp", Environment.ProcessId.ToString());
                File.Move(args[1] + ".tmp", args[1]);
                await Task.Delay(TimeSpan.FromMinutes(1));
                return 0;
            case "tree":
                File.WriteAllText(args[1] + ".parent", Environment.ProcessId.ToString());
                var fixture = ChildArguments("wait", args[1]);
                var start = new ProcessStartInfo(fixture.Tool) { UseShellExecute = false, CreateNoWindow = true };
                foreach (string arg in fixture.Arguments) start.ArgumentList.Add(arg);
                using (var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start child fixture."))
                    await child.WaitForExitAsync();
                return 0;
            case "failure":
                Console.Error.WriteLine("fixture diagnostic");
                return 17;
            case "download-progress":
                Console.WriteLine(DownloadProgress.DownloadPrefix + """{"status":"downloading","downloaded_bytes":50,"total_bytes":100,"speed":10,"eta":5}""");
                await Task.Delay(50);
                Console.Error.WriteLine("[Merger] Merging formats into fixture.mkv");
                return 0;
            default: throw new ArgumentException("Unknown process fixture.");
        }
    }
}
