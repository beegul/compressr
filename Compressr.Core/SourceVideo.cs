using System.ComponentModel;

namespace Compressr.Core;

public sealed class SourceVideo(string input) : INotifyPropertyChanged
{
    public bool IsLocal { get; } = File.Exists(input) || Path.IsPathFullyQualified(input);
    private Task<YouTubeInfo>? lookup;
    private CancellationTokenSource? lookupCancellation;
    public string Input { get; } = input;
    public int Rotation { get; set; }
    public string? CachedSource { get; set; }
    public YouTubeInfo? Metadata { get; private set; }
    public string TitleStatus { get; private set; } = "";
    public bool IsResolvingTitle => lookupCancellation != null;
    public bool CanRetryTitle => !IsLocal && Metadata == null && !IsResolvingTitle;
    public string Label => IsLocal ? Path.GetFileName(Input) : Metadata?.Title ?? Input;
    public string Detail => IsLocal ? Input : TitleStatus.Length > 0 ? $"{Input}\n{TitleStatus}" : Input;
    public event PropertyChangedEventHandler? PropertyChanged;
    public override string ToString() => Label;

    public Task<YouTubeInfo> MetadataAsync(Func<string, CancellationToken, Task<YouTubeInfo>> resolve,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (IsLocal) throw new InvalidOperationException("Local files use media probing, not YouTube metadata.");
        if (Metadata != null) return Task.FromResult(Metadata);
        if (lookup == null || lookup.IsCompleted) lookup = LoadAsync(resolve);
        return lookup.WaitAsync(token);
    }

    public void CancelTitle() => lookupCancellation?.Cancel();

    private async Task<YouTubeInfo> LoadAsync(Func<string, CancellationToken, Task<YouTubeInfo>> resolve)
    {
        using var pending = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        lookupCancellation = pending;
        TitleStatus = "Looking up title (no video download)...";
        Notify();
        try
        {
            var metadata = await resolve(Input, pending.Token);
            pending.Token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(metadata.Title))
                throw new InvalidDataException("YouTube did not provide a video title.");
            Metadata = metadata;
            TitleStatus = "";
            return metadata;
        }
        catch (OperationCanceledException)
        {
            TitleStatus = "Title lookup cancelled or timed out. Use Retry title.";
            throw;
        }
        catch (Exception ex)
        {
            TitleStatus = $"Title unavailable: {ex.Message} Use Retry title.";
            throw;
        }
        finally
        {
            lookupCancellation = null;
            Notify();
        }
    }

    private void Notify()
    {
        foreach (string property in new[] { nameof(Label), nameof(Detail), nameof(TitleStatus), nameof(IsResolvingTitle), nameof(CanRetryTitle) })
            PropertyChanged?.Invoke(this, new(property));
    }
}
