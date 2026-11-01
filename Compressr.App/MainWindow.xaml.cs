using System.ComponentModel;
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
    private string? cachedSource;
    private string? cachedInput;
    private readonly List<string> previewFiles = [];

    public MainWindow()
    {
        InitializeComponent();
        Standard.ItemsSource = Enum.GetValues<TelevisionSystem>();
        Capacity.ItemsSource = Enum.GetValues<DiscSize>();
        Output.ItemsSource = new[] { "Burn to DVD", "Save ISO", "Save DVD folder" };
        Standard.SelectedItem = TelevisionSystem.PAL;
        Capacity.SelectedItem = DiscSize.SingleLayer;
        Output.SelectedIndex = 1;
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
    }
    private void OutputChanged(object sender, SelectionChangedEventArgs e)
    { if (Drives != null) Drives.IsEnabled = Output.SelectedIndex == 0; }
    private void RefreshDrives(object sender, RoutedEventArgs e) => LoadDrives();
    private void LoadDrives()
    {
        try
        {
            Drives.ItemsSource = DiscWriter.GetDrives();
            Drives.SelectedIndex = 0;
            Drives.IsEnabled = Output.SelectedIndex == 0;
        }
        catch (Exception ex) { Status.Text = $"DVD drive detection failed: {ex.Message}. ISO/folder output is still available."; }
    }
    private void Browse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Video files|*.mp4;*.mkv;*.mov;*.avi;*.wmv;*.webm;*.mpeg;*.mpg;*.m4v;*.mts;*.m2ts|All files|*.*" };
        if (dialog.ShowDialog(this) == true) Source.Text = dialog.FileName;
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
    { rotation = 0; cachedSource = cachedInput = null; if (Preview != null) Preview.Source = null; if (RotationLabel != null) RotationLabel.Text = "Extra rotation: 0°"; }
    private void OnDragOver(object sender, DragEventArgs e)
    { e.Effects = cancellation == null && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private void OnDrop(object sender, DragEventArgs e)
    {
        if (cancellation != null) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length == 1) Source.Text = files[0];
        else Status.Text = "Drop exactly one local video file.";
    }
    private IProgress<StageProgress> Reporter() => new Progress<StageProgress>(p =>
    { Stage.Text = p.Stage; Status.Text = p.Detail; Progress.IsIndeterminate = p.Fraction == null; Progress.Value = (p.Fraction ?? 0) * 100; });
    private async Task<string> ResolveAsync(MediaTools tools, IProgress<StageProgress> progress, CancellationToken token)
    {
        string input = Source.Text.Trim();
        if (cachedInput == input && cachedSource != null && File.Exists(cachedSource)) return cachedSource;
        if (File.Exists(input)) return input;
        MediaTools.ValidateYouTube(input);
        cachedSource = await tools.DownloadAsync(input, Path.Combine(stateDirectory, "downloads", Guid.NewGuid().ToString("N")), progress, token);
        cachedInput = input;
        return cachedSource;
    }
    private void Busy(bool value)
    {
        Options.IsEnabled = !value; StartButton.IsEnabled = !value; CancelButton.IsEnabled = value && !burning;
    }
    private async void ShowPreview(object sender, RoutedEventArgs e) => await PreviewAsync();
    private async void Rotate(object sender, RoutedEventArgs e)
    { rotation = (rotation + 90) % 360; RotationLabel.Text = $"Extra rotation: {rotation}°"; await PreviewAsync(); }
    private async Task PreviewAsync()
    {
        if (cancellation != null) return;
        cancellation = new(); Busy(true);
        try
        {
            var tools = Tools(); var reporter = Reporter();
            string source = await ResolveAsync(tools, reporter, cancellation.Token);
            var info = await tools.ProbeAsync(source, cancellation.Token);
            // Preview remains available even when the selected disc cannot fit the source.
            var plan = new DvdPlan((TelevisionSystem)Standard.SelectedItem, 0, 0,
                info.DisplayAspect(rotation) >= 1.55, 720, Standard.SelectedItem.Equals(TelevisionSystem.PAL) ? 576 : 480);
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
        if (cancellation != null) return;
        string destination;
        if (Output.SelectedIndex == 2)
        {
            var dialog = new OpenFolderDialog { Title = "Choose an output parent folder; a new Compressr DVD folder will be created" };
            if (dialog.ShowDialog(this) != true) return;
            destination = Path.Combine(dialog.FolderName, $"Compressr-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}");
        }
        else if (Output.SelectedIndex == 1)
        {
            var dialog = new SaveFileDialog { Filter = "DVD image|*.iso", FileName = "Compressr.iso", OverwritePrompt = true };
            if (dialog.ShowDialog(this) != true) return;
            destination = dialog.FileName;
        }
        else destination = "";
        int output = Output.SelectedIndex;
        var drive = Drives.SelectedItem as DvdDrive;
        if (output == 0 && drive == null) { Status.Text = "Select a DVD writer with a blank DVD, or choose ISO/folder output."; return; }
        if (output == 0 && MessageBox.Show(this, "Write and finalise the blank DVD in the selected drive? This cannot be undone. Compressr never erases discs.",
            "Confirm DVD burning", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        cancellation = new(); Busy(true);
        string work = Path.Combine(stateDirectory, "jobs", Guid.NewGuid().ToString("N"));
        string? partialIso = null;
        try
        {
            var token = cancellation.Token; var reporter = Reporter(); var tools = Tools();
            var source = await ResolveAsync(tools, reporter, token);
            Stage.Text = "Inspect"; Status.Text = "Checking duration, orientation and disc fit…";
            var info = await tools.ProbeAsync(source, token);
            var plan = DvdPlan.Create(info, (TelevisionSystem)Standard.SelectedItem, (DiscSize)Capacity.SelectedItem, rotation);
            if (output == 0) DiscWriter.CheckBlank(drive!, plan.Capacity * 95 / 100);
            var dvd = await new DvdPipeline(tools).AuthorAsync(source, info, plan, rotation, work, reporter, token);
            if (output == 2)
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
                partialIso = output == 1 ? destination + $".{Guid.NewGuid():N}.partial" : Path.Combine(work, "disc.iso");
                await tools.CreateIsoAsync(dvd, partialIso, plan, reporter, token);
                token.ThrowIfCancellationRequested();
                if (output == 1) { File.Move(partialIso, destination, true); partialIso = null; }
                else
                {
                    burning = true; CancelButton.IsEnabled = false;
                    // IMAPI COM calls run on their own STA, never on WPF's UI thread.
                    await RunStaAsync(() => DiscWriter.Burn(partialIso!, drive!, reporter));
                    await DiscWriter.VerifyAsync(dvd, drive!, reporter);
                }
            }
            Stage.Text = "Done"; Progress.IsIndeterminate = false; Progress.Value = 100;
            Status.Text = output == 0 ? "DVD burned, finalised and VIDEO_TS files verified by SHA-256 read-back."
                : $"Saved to {destination}\nReady for DVD playback; physical player compatibility has not been verified.";
        }
        catch (OperationCanceledException) { Stage.Text = "Cancelled"; Status.Text = $"No disc was written. Work files retained at {work}." + (output == 2 ? $"\nAny partially saved folder at {destination} is not a complete DVD." : ""); }
        catch (Exception ex) { Stage.Text = burning ? "Disc not verified" : "Could not make DVD"; Status.Text = $"{ex.Message}\nWork files retained at {work}." + (output == 2 ? $"\nAny partially saved folder at {destination} is not a complete DVD." : ""); }
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
