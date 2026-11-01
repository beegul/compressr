using System.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using Compressr.Core;
using Microsoft.Win32;

namespace Compressr.App;

public partial class MainWindow : Window
{
    private readonly string stateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Compressr");
    private CancellationTokenSource? cancellation;
    private bool burning;
    private bool initialized;
    private int rotation;
    private sealed class InputVideo(string input)
    {
        public string Input { get; } = input;
        public int Rotation { get; set; }
        public string? CachedSource { get; set; }
        public string Label => File.Exists(Input) ? Path.GetFileName(Input) : Input;
        public override string ToString() => Label;
    }
    private readonly ObservableCollection<InputVideo> videos = [];
    private readonly List<string> previewFiles = [];

    public MainWindow()
    {
        InitializeComponent();
        Inputs.ItemsSource = videos;
        Standard.ItemsSource = Enum.GetValues<TelevisionSystem>();
        Capacity.ItemsSource = Enum.GetValues<DiscSize>();
        Output.ItemsSource = new[] { "DVD-ready ISO (burn later)", "DVD folder" };
        Standard.SelectedItem = TelevisionSystem.PAL;
        Capacity.SelectedItem = DiscSize.SingleLayer;
        Output.SelectedIndex = 0;
        Directory.CreateDirectory(stateDirectory);
        try
        {
            string settings = Path.Combine(stateDirectory, "settings.json");
            if (File.Exists(settings))
            {
                var saved = JsonSerializer.Deserialize<TelevisionSystem>(File.ReadAllText(settings));
                if (!Enum.IsDefined(saved)) throw new InvalidDataException("Saved TV standard is not PAL or NTSC.");
                Standard.SelectedItem = saved;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        { Status.Text = $"Could not read saved TV standard: {ex.Message}"; }
        initialized = true;
        LoadDrives();
    }

    private MediaTools Tools()
    {
        string? configured = Environment.GetEnvironmentVariable("COMPRESSR_TOOLS");
        if (!string.IsNullOrWhiteSpace(configured)) return new(configured);
        var path = new DirectoryInfo(AppContext.BaseDirectory);
        while (path != null)
        {
            if (Directory.Exists(Path.Combine(path.FullName, "tools"))) return new(Path.Combine(path.FullName, "tools"));
            path = path.Parent;
        }
        return new(Path.Combine(AppContext.BaseDirectory, "tools"));
    }

    private void SettingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized) return;
        try { File.WriteAllText(Path.Combine(stateDirectory, "settings.json"), JsonSerializer.Serialize((TelevisionSystem)Standard.SelectedItem)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status.Text = $"Could not save TV standard: {ex.Message}"; }
        Preview.Source = null;
        Estimate.Text = "TV standard changed. Check fit to refresh estimates.";
    }
    private void RefreshDrives(object sender, RoutedEventArgs e) => LoadDrives();
    private async void CheckDisc(object sender, RoutedEventArgs e)
    {
        if (cancellation != null) return;
        if (Drives.SelectedItem is not DvdDrive drive) { Status.Text = "Select a DVD writer first."; return; }
        cancellation = new(); Busy(true);
        try
        {
            DvdMedia? media = null;
            await RunStaAsync(() => media = DiscWriter.Inspect(drive));
            cancellation.Token.ThrowIfCancellationRequested();
            media!.Check(2048);
            Stage.Text = "Disc checked (not written)";
            Status.Text = $"{drive.Label}\n{media.Summary}\n" +
                "Supported for burning; select PAL for UK DVD-Video. Actual playback depends on the player's support for this recordable format.";
        }
        catch (OperationCanceledException) { Stage.Text = "Cancelled"; }
        catch (Exception ex) { Stage.Text = "Disc check failed"; Status.Text = ex.Message; }
        finally { cancellation.Dispose(); cancellation = null; Busy(false); }
    }
    private void LoadDrives()
    {
        try
        {
            Drives.ItemsSource = DiscWriter.GetDrives();
            Drives.SelectedIndex = 0;
        }
        catch (Exception ex) { Status.Text = $"DVD drive detection failed: {ex.Message}. ISO/folder output is still available."; }
    }
    private void Browse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Filter = "Video files|*.mp4;*.mkv;*.mov;*.avi;*.wmv;*.webm;*.mpeg;*.mpg;*.m4v;*.mts;*.m2ts|All files|*.*" };
        if (dialog.ShowDialog(this) == true)
            foreach (string file in dialog.FileNames) AddVideo(file);
    }
    private void SaveScreenshot(object sender, RoutedEventArgs e)
    {
        try
        {
            var content = (FrameworkElement)Content;
            var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string path = Path.Combine(stateDirectory, "screenshot.png");
            using (var output = File.Create(path)) encoder.Save(output);
            Status.Text = $"Screenshot saved to {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { Status.Text = $"Could not save screenshot: {ex.Message}"; }
    }
    private void SourceChanged(object sender, TextChangedEventArgs e)
    { if (initialized && Source.Text.Length > 0) Estimate.Text = "Input changed. Add it, then Check fit for the combined duration."; }
    private void AddVideo(string input)
    {
        videos.Add(new(input));
        Inputs.SelectedIndex = videos.Count - 1;
        Estimate.Text = "List changed. Check fit before making the DVD.";
    }
    private bool AddPending()
    {
        string input = Source.Text.Trim();
        if (input.Length > 0)
        {
            try { if (!File.Exists(input)) MediaTools.ValidateYouTube(input); }
            catch (ArgumentException ex) { Status.Text = ex.Message; return false; }
            AddVideo(input); Source.Clear();
        }
        if (videos.Count > 0) return true;
        Status.Text = "Add at least one local video or YouTube link."; return false;
    }
    private void AddInput(object sender, RoutedEventArgs e) => AddPending();
    private void InputSelected(object sender, SelectionChangedEventArgs e)
    {
        rotation = (Inputs.SelectedItem as InputVideo)?.Rotation ?? 0;
        if (Preview != null) Preview.Source = null;
        if (RotationLabel != null) RotationLabel.Text = $"Extra rotation: {rotation}°";
    }
    private void MoveUp(object sender, RoutedEventArgs e) => MoveInput(-1);
    private void MoveDown(object sender, RoutedEventArgs e) => MoveInput(1);
    private void MoveInput(int delta)
    {
        int index = Inputs.SelectedIndex;
        if (index < 0 || index + delta < 0 || index + delta >= videos.Count) return;
        videos.Move(index, index + delta); Inputs.SelectedIndex = index + delta;
    }
    private void RemoveInput(object sender, RoutedEventArgs e)
    {
        int index = Inputs.SelectedIndex;
        if (index < 0) return;
        videos.RemoveAt(index); Inputs.SelectedIndex = Math.Min(index, videos.Count - 1);
        Estimate.Text = "List changed. Check fit for the updated duration.";
    }
    private void OnDragOver(object sender, DragEventArgs e)
    { e.Effects = cancellation == null && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private void OnDrop(object sender, DragEventArgs e)
    {
        if (cancellation != null) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            foreach (var file in files) AddVideo(file);
    }
    private IProgress<StageProgress> Reporter() => new Progress<StageProgress>(p =>
    { Stage.Text = p.Stage; Status.Text = p.Detail; Progress.IsIndeterminate = p.Fraction == null; Progress.Value = (p.Fraction ?? 0) * 100; });
    private async Task<string> ResolveAsync(InputVideo item, MediaTools tools, IProgress<StageProgress> progress, CancellationToken token)
    {
        string input = item.Input;
        if (item.CachedSource != null && File.Exists(item.CachedSource)) return item.CachedSource;
        if (File.Exists(input)) return input;
        MediaTools.ValidateYouTube(input);
        item.CachedSource = await tools.DownloadAsync(input, Path.Combine(stateDirectory, "downloads", Guid.NewGuid().ToString("N")), progress, token);
        return item.CachedSource;
    }
    private async Task<VideoInfo> EstimateAsync(MediaTools tools, CancellationToken token)
    {
        var clips = new List<VideoClip>();
        foreach (var item in videos)
        {
            Stage.Text = "Inspect"; Status.Text = $"Checking {item.Label} (no YouTube download)…";
            VideoInfo info;
            string source = item.CachedSource != null && File.Exists(item.CachedSource) ? item.CachedSource : item.Input;
            if (File.Exists(source)) info = await tools.ProbeAsync(source, token);
            else
            {
                var metadata = await tools.YouTubeMetadataAsync(item.Input, token);
                info = new(metadata.Duration, 1920, 1080, 1, 0, true);
            }
            clips.Add(new(item.Input, info, item.Rotation));
        }
        var combined = VideoPlaylist.Combined(clips);
        Estimate.Text = VideoPlaylist.FitEstimate(combined, (TelevisionSystem)Standard.SelectedItem);
        return combined;
    }
    private async void CheckFit(object sender, RoutedEventArgs e)
    {
        if (cancellation != null || !AddPending()) return;
        cancellation = new(); Busy(true);
        try { await EstimateAsync(Tools(), cancellation.Token); Stage.Text = "Fit checked"; Status.Text = Estimate.Text; }
        catch (OperationCanceledException) { Stage.Text = "Cancelled"; }
        catch (Exception ex) { Stage.Text = "Fit check failed"; Estimate.Text = "Fit is unknown."; Status.Text = ex.Message; }
        finally { cancellation.Dispose(); cancellation = null; Busy(false); }
    }
    private void Busy(bool value)
    {
        Options.IsEnabled = !value; BurnOptions.IsEnabled = !value;
        StartButton.IsEnabled = !value; CancelButton.IsEnabled = value && !burning;
    }
    private async void ShowPreview(object sender, RoutedEventArgs e) => await PreviewAsync();
    private async void Rotate(object sender, RoutedEventArgs e)
    {
        if (!AddPending()) return;
        rotation = (rotation + 90) % 360;
        ((InputVideo)Inputs.SelectedItem).Rotation = rotation;
        RotationLabel.Text = $"Extra rotation: {rotation}°"; await PreviewAsync();
    }
    private async Task PreviewAsync()
    {
        if (cancellation != null || !AddPending()) return;
        cancellation = new(); Busy(true);
        try
        {
            var tools = Tools(); var reporter = Reporter();
            var selected = (InputVideo)Inputs.SelectedItem;
            string source = await ResolveAsync(selected, tools, reporter, cancellation.Token);
            var info = await tools.ProbeAsync(source, cancellation.Token);
            // Preview remains available even when the selected disc cannot fit the source.
            var plan = new DvdPlan((TelevisionSystem)Standard.SelectedItem, 0, 0,
                videos.Count > 1 || info.DisplayAspect(rotation) >= 1.55, 720, Standard.SelectedItem.Equals(TelevisionSystem.PAL) ? 576 : 480);
            string frame = Path.Combine(stateDirectory, $"preview-{Guid.NewGuid():N}.png");
            previewFiles.Add(frame);
            await tools.RunAsync(tools.Ffmpeg, ["-y", "-nostdin", "-ss", Math.Min(3, info.Duration / 2).ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-i", source, "-vf", plan.Filter(rotation, true), "-frames:v", "1", frame], cancellation.Token);
            var image = new BitmapImage();
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(frame); image.EndInit(); image.Freeze();
            Preview.Source = image;
            Stage.Text = "Preview ready"; Status.Text = $"{info.Duration / 60:F1} minutes • {plan.Aspect} • metadata rotation + {rotation}° manual";
        }
        catch (OperationCanceledException) { Stage.Text = "Cancelled"; }
        catch (Exception ex) { Stage.Text = "Preview failed"; Status.Text = ex.Message; }
        finally { cancellation.Dispose(); cancellation = null; Progress.IsIndeterminate = false; Busy(false); }
    }
    private async void Start(object sender, RoutedEventArgs e)
    {
        if (cancellation != null || !AddPending()) return;
        string destination;
        if (Output.SelectedIndex == 1)
        {
            var dialog = new OpenFolderDialog { Title = "Choose an output parent folder; a new Compressr DVD folder will be created" };
            if (dialog.ShowDialog(this) != true) return;
            destination = Path.Combine(dialog.FolderName, $"Compressr-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}");
        }
        else
        {
            var dialog = new SaveFileDialog { Filter = "DVD image|*.iso", FileName = "Compressr.iso", OverwritePrompt = true };
            if (dialog.ShowDialog(this) != true) return;
            destination = dialog.FileName;
        }
        int output = Output.SelectedIndex;
        cancellation = new(); Busy(true);
        string work = Path.Combine(stateDirectory, "jobs", Guid.NewGuid().ToString("N"));
        string? partialIso = null;
        try
        {
            var token = cancellation.Token; var reporter = Reporter(); var tools = Tools();
            var estimate = await EstimateAsync(tools, token);
            _ = DvdPlan.Create(estimate, (TelevisionSystem)Standard.SelectedItem, (DiscSize)Capacity.SelectedItem, 0);
            var clips = new List<VideoClip>();
            foreach (var item in videos)
            {
                var source = await ResolveAsync(item, tools, reporter, token);
                clips.Add(new(source, await tools.ProbeAsync(source, token), item.Rotation));
            }
            Stage.Text = "Inspect"; Status.Text = "Checking duration, orientation and disc fit…";
            var info = VideoPlaylist.Combined(clips);
            Estimate.Text = VideoPlaylist.FitEstimate(info, (TelevisionSystem)Standard.SelectedItem);
            var plan = DvdPlan.Create(info, (TelevisionSystem)Standard.SelectedItem, (DiscSize)Capacity.SelectedItem, 0);
            var dvd = await new DvdPipeline(tools).AuthorPlaylistAsync(clips, plan, work, reporter, token);
            if (output == 1)
            {
                token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(destination);
                foreach (var folder in Directory.GetDirectories(dvd, "*", SearchOption.AllDirectories))
                    Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(dvd, folder)));
                var files = Directory.GetFiles(dvd, "*", SearchOption.AllDirectories);
                for (int i = 0; i < files.Length; i++)
                {
                    reporter.Report(new("Save", (double)i / files.Length, $"Saving {Path.GetFileName(files[i])}…"));
                    await using var input = File.OpenRead(files[i]);
                    await using var saved = new FileStream(Path.Combine(destination, Path.GetRelativePath(dvd, files[i])),
                        FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                    await input.CopyToAsync(saved, token);
                }
            }
            else
            {
                partialIso = destination + $".{Guid.NewGuid():N}.partial";
                await tools.CreateIsoAsync(dvd, partialIso, plan, reporter, token);
                using (var prepared = await PreparedDvd.OpenAsync(partialIso, reporter, token))
                    await prepared.VerifyDirectoryAsync(Path.Combine(dvd, "VIDEO_TS"), reporter, token);
                token.ThrowIfCancellationRequested();
                File.Move(partialIso, destination, true); partialIso = null;
            }
            Stage.Text = "Done"; Progress.IsIndeterminate = false; Progress.Value = 100;
            Status.Text = $"Saved to {destination}\n" + (output == 0
                ? "DVD-ready ISO verified. Keep this file; reopen it with Burn prepared DVD whenever you have a blank disc. Original videos and work folders are not needed."
                : "DVD folder saved. Choose ISO output for the prepare-now / burn-later workflow.");
        }
        catch (OperationCanceledException) { Stage.Text = "Cancelled"; Status.Text = $"No disc was written. Work files retained at {work}." + (output == 1 ? $"\nAny partially saved folder at {destination} is not a complete DVD." : ""); }
        catch (Exception ex) { Stage.Text = "Could not prepare DVD"; Status.Text = $"{ex.Message}\nWork files retained at {work}." + (output == 1 ? $"\nAny partially saved folder at {destination} is not a complete DVD." : ""); }
        finally
        {
            if (partialIso != null && File.Exists(partialIso) && !burning)
            {
                try { File.Delete(partialIso); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status.Text += $"\nCould not remove partial image: {ex.Message}"; }
            }
            burning = false; cancellation.Dispose(); cancellation = null; Progress.IsIndeterminate = false; Busy(false);
        }
    }
    private async void BurnPrepared(object sender, RoutedEventArgs e)
    {
        if (cancellation != null) return;
        var dialog = new OpenFileDialog { Title = "Open a prepared Compressr DVD ISO", Filter = "DVD-ready ISO|*.iso" };
        if (dialog.ShowDialog(this) != true) return;
        cancellation = new(); Busy(true);
        try
        {
            var reporter = Reporter();
            using var prepared = await PreparedDvd.OpenAsync(dialog.FileName, reporter, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            var drive = Drives.SelectedItem as DvdDrive;
            if (drive == null)
            {
                Stage.Text = "Ready to burn"; Progress.Value = 100;
                Status.Text = $"Validated {prepared.Path} ({prepared.Length / 1_000_000.0:F1} MB).\nNo DVD writer selected. Connect a writer, refresh drives, insert a blank DVD, then open this ISO again. No source files are needed.";
                return;
            }
            await RunStaAsync(() => DiscWriter.CheckBlank(drive, prepared.Length));
            cancellation.Token.ThrowIfCancellationRequested();
            if (MessageBox.Show(this, $"Write and finalise {Path.GetFileName(prepared.Path)} to {drive.Label}?\nThis cannot be undone. Compressr never erases discs.",
                "Confirm DVD burning", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            { Stage.Text = "Not written"; Status.Text = "Burn declined. The prepared ISO is unchanged."; return; }
            burning = true; CancelButton.IsEnabled = false;
            // IMAPI COM calls run on their own STA, never on WPF's UI thread.
            await RunStaAsync(() => DiscWriter.Burn(prepared, drive, reporter));
            await DiscWriter.VerifyAsync(prepared, drive, reporter);
            Stage.Text = "Done"; Progress.Value = 100;
            Status.Text = "DVD burned, finalised and VIDEO_TS files verified against the saved ISO by SHA-256 read-back.";
        }
        catch (OperationCanceledException) { Stage.Text = "Cancelled"; Status.Text = "No disc was written. The prepared ISO is unchanged."; }
        catch (Exception ex) { Stage.Text = burning ? "Disc not verified" : "Could not burn prepared DVD"; Status.Text = ex.Message; }
        finally { burning = false; cancellation.Dispose(); cancellation = null; Progress.IsIndeterminate = false; Busy(false); }
    }
    private static Task RunStaAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); completion.SetResult(); } catch (Exception ex) { completion.SetException(ex); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
    private void Cancel(object sender, RoutedEventArgs e) { if (!burning) cancellation?.Cancel(); }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (cancellation != null) { e.Cancel = true; Status.Text = burning ? "Wait until disc writing and verification finish." : "Cancel the current operation before closing."; return; }
        foreach (string file in previewFiles)
        {
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { System.Diagnostics.Trace.TraceWarning($"Could not remove preview {file}: {ex.Message}"); }
        }
    }
}
