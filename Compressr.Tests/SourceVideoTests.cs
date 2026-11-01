using System.Collections.ObjectModel;
using Compressr.Core;

internal static class SourceVideoTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var first = new SourceVideo("https://youtu.be/first?t=30") { Rotation = 90, CachedSource = "cached.mkv" };
        var second = new SourceVideo("https://youtu.be/second");
        var queue = new ObservableCollection<SourceVideo> { first, second };
        var firstResult = new TaskCompletionSource<YouTubeInfo>();
        var secondResult = new TaskCompletionSource<YouTubeInfo>();
        int calls = 0, notifications = 0;
        first.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SourceVideo.Label)) notifications++; };
        Task<YouTubeInfo> Lookup(string url, CancellationToken token)
        {
            calls++;
            return (url == first.Input ? firstResult.Task : secondResult.Task).WaitAsync(token);
        }
        var a = first.MetadataAsync(Lookup, CancellationToken.None);
        var reused = first.MetadataAsync(Lookup, CancellationToken.None);
        var b = second.MetadataAsync(Lookup, CancellationToken.None);
        check(calls == 2 && first.IsResolvingTitle && first.TitleStatus.Contains("no video download"),
            "title requests are shared per row and pending is explicit");
        queue.Move(1, 0);
        secondResult.SetResult(new("Part two", 120));
        await b;
        firstResult.SetResult(new("Part one \u2013 caf\u00e9", 60));
        await Task.WhenAll(a, reused);
        check(queue[0] == second && queue[1] == first, "out-of-order title completion never sorts or replaces source rows");
        check(first.Label == "Part one \u2013 caf\u00e9" && notifications >= 2 && first.TitleStatus == "",
            "resolved Unicode title notifies bindings and clears pending status");
        check(first.Input == "https://youtu.be/first?t=30" && first.Detail == first.Input &&
            first.Rotation == 90 && first.CachedSource == "cached.mkv", "title updates preserve exact URL, rotation and downloaded-source cache");
        var cached = await first.MetadataAsync(Lookup, CancellationToken.None);
        check(calls == 2 && cached.Duration == 60, "fit inspection reuses resolved title and duration without another lookup");

        var failed = new SourceVideo("https://youtu.be/unavailable");
        try { await failed.MetadataAsync((_, _) => Task.FromException<YouTubeInfo>(new IOException("Offline")), CancellationToken.None); }
        catch (IOException) { }
        check(failed.Metadata == null && failed.Label == failed.Input && failed.Detail.Contains("Offline") &&
            failed.TitleStatus.Contains("Retry title"), "lookup failure retains URL and visible diagnostic/retry guidance");
        await failed.MetadataAsync((_, _) => Task.FromResult(new YouTubeInfo("Recovered", 20)), CancellationToken.None);
        check(failed.Label == "Recovered" && failed.TitleStatus == "", "failed title lookup can be retried");

        var cancelled = new SourceVideo("https://youtu.be/cancel");
        var late = new TaskCompletionSource<YouTubeInfo>();
        var pending = cancelled.MetadataAsync((_, token) => late.Task.WaitAsync(token), CancellationToken.None);
        cancelled.CancelTitle();
        try { await pending; check(false, "title cancellation"); }
        catch (OperationCanceledException) { check(true, "title cancellation reaches resolver"); }
        late.SetResult(new("Must not appear", 20));
        check(cancelled.Metadata == null && !cancelled.IsResolvingTitle && cancelled.TitleStatus.Contains("cancelled"),
            "cancelled lookup never applies a late result");
        await cancelled.MetadataAsync((_, _) => Task.FromResult(new YouTubeInfo("Retry", 20)), CancellationToken.None);
        check(cancelled.Label == "Retry", "cancelled title lookup can be retried");

        var local = new SourceVideo(Path.Combine(Path.GetTempPath(), "local-video.mp4"));
        check(local.IsLocal && local.Label == "local-video.mp4" && local.TitleStatus == "",
            "local file labels do not request remote metadata");
        var invalidTitle = new SourceVideo("https://youtu.be/untitled");
        try { await invalidTitle.MetadataAsync((_, _) => Task.FromResult(new YouTubeInfo(" ", 20)), CancellationToken.None); }
        catch (InvalidDataException) { }
        check(invalidTitle.Metadata == null && invalidTitle.TitleStatus.Contains("did not provide"),
            "missing title is an explicit failure rather than a misleading successful label");
    }
}
