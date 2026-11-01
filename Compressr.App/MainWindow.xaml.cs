using System.ComponentModel;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using Compressr.Core;
using Microsoft.Win32;

namespace Compressr.App;

public partial class MainWindow : Window
{
    private readonly string stateDirectory;
    private CancellationTokenSource? cancellation;
    private bool burning;
    private bool erasing;
    private readonly DiscPreparation discPreparation = new();
    private bool initialized;
    private bool updatingOutput;
    private bool updatingDrives;
    private readonly DvdOutputSelection outputSelection = new();
    private DiscReadiness readiness = DiscReadiness.NoWriter;
    private string isoDestination = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Compressr.iso");
    private string folderDestination = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), $"Compressr-DVD-{DateTime.Now:yyyyMMdd-HHmmss}");
    private sealed record PreparedJob(string Path, string Status)
    {
        public string Name => System.IO.Path.GetFileName(Path);
        public override string ToString() => Name;
    }
    private readonly ObservableCollection<PreparedJob> preparedJobs = [];
    private int rotation;
    private readonly ObservableCollection<SourceVideo> videos = [];
    private readonly SemaphoreSlim titleSlots = new(2);
    private readonly Func<string, CancellationToken, Task<YouTubeInfo>> metadataLookup;
    private readonly List<string> previewFiles = [];
    private Point dragOrigin;
    private SourceVideo? draggedVideo;
    private QueueDropMarker? dropMarker;
    private long lastDragScroll;

    public MainWindow() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Compressr")) { }

    internal MainWindow(string stateDirectory) : this(stateDirectory, null) { }

    internal MainWindow(string stateDirectory, Func<string, CancellationToken, Task<YouTubeInfo>>? metadataLookup)
    {
        this.stateDirectory = stateDirectory;
        this.metadataLookup = metadataLookup ?? LookupMetadataAsync;
        InitializeComponent();
        Inputs.ItemsSource = videos;
        videos.CollectionChanged += (_, _) => QueueSummary.Text = videos.Count switch
        {
            0 => "Play order",
            1 => "1 video · plays in full",
            _ => $"{videos.Count} videos · play in this order"
        };
        Standard.ItemsSource = Enum.GetValues<TelevisionSystem>();
        Capacity.ItemsSource = Enum.GetValues<DiscSize>();
        Output.ItemsSource = new[] { "Save DVD-ready ISO", "Save DVD folder", "Prepare and burn DVD" };
        PreparedJobs.ItemsSource = preparedJobs;
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
        LoadPreparedJobs();
        UpdateOutput();
        Loaded += async (_, _) => await RefreshReadinessAsync(true);
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

    private void WorkspaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool compact = e.NewSize.Height < 760 || e.NewSize.Width < 800;
        ProductPromise.Visibility = SourceHeading.Visibility = SourceHelp.Visibility =
            compact ? Visibility.Collapsed : Visibility.Visible;
        ProductHeader.Padding = new Thickness(24, compact ? 10 : 14, 24, compact ? 10 : 14);
        SourceEntry.Padding = new Thickness(compact ? 12 : 16);
        QueueInstructions.Text = compact ? "Drag to reorder · Alt+↑ / Alt+↓ with keyboard." :
            "Drag clips to set play order · Alt+↑ / Alt+↓ with keyboard. Whole videos are joined; link timestamps are ignored.";
    }

    private void SettingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized) return;
        try { File.WriteAllText(Path.Combine(stateDirectory, "settings.json"), JsonSerializer.Serialize((TelevisionSystem)Standard.SelectedItem)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status.Text = $"Could not save TV standard: {ex.Message}"; }
        Preview.Source = null;
        Estimate.Text = "TV standard changed. Check fit to refresh estimates.";
    }
    private long RequiredBurnBytes() => DvdPlan.DiscCapacity((DiscSize)Capacity.SelectedItem) / 2048 * 2048;

    private async void RefreshDrives(object sender, RoutedEventArgs e) => await RefreshReadinessAsync(true);
    private async void CheckDisc(object sender, RoutedEventArgs e) => await RefreshReadinessAsync(false);
    private async void DriveChanged(object sender, SelectionChangedEventArgs e)
    { if (initialized && !updatingDrives) await RefreshReadinessAsync(false); }
    private async void CapacityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized) return;
        Estimate.Text = "Capacity changed. Check fit before preparing.";
        await RefreshReadinessAsync(false);
    }
    private async Task RefreshReadinessAsync(bool enumerate)
    {
        if (cancellation != null) return;
        cancellation = new(); Busy(true);
        try
        {
            Stage.Text = "Checking disc (read only)";
            long required = RequiredBurnBytes();
            DvdDrive? selected = Drives.SelectedItem as DvdDrive;
            IReadOnlyList<DvdDrive> drives = [];
            var results = new Dictionary<string, DiscReadiness>();
            var inspections = new Dictionary<string, DiscInspection>();
            await RunStaAsync(() =>
            {
                drives = enumerate ? DiscWriter.GetDrives() : selected == null ? [] : [selected];
                foreach (var drive in drives)
                {
                    try
                    {
                        var inspection = DiscWriter.InspectPreparation(drive);
                        inspections[drive.Id] = inspection;
                        results[drive.Id] = DiscReadiness.Assess(inspection.Media, required);
                        if (!results[drive.Id].Ready && inspection.NeedsErase)
                        {
                            inspection.CheckForPreparation(required);
                            results[drive.Id] = new(false, $"{inspection.Media.Name}: preparation required. Erasing removes all contents and needs separate confirmation. Automatic will not erase.");
                        }
                    }
                    catch (Exception ex) { results[drive.Id] = DiscReadiness.Failed($"{drive.Label}: {ex.Message}"); }
                }
            });
            cancellation.Token.ThrowIfCancellationRequested();
            if (enumerate)
            {
                updatingDrives = true;
                try
                {
                    Drives.ItemsSource = drives;
                    var previous = drives.FirstOrDefault(d => d.Id == selected?.Id);
                    Drives.SelectedItem = !outputSelection.IsManual
                        ? drives.FirstOrDefault(d => results[d.Id].Ready) ?? previous ?? drives.FirstOrDefault()
                        : previous ?? drives.FirstOrDefault(d => results[d.Id].Ready) ?? drives.FirstOrDefault();
                }
                finally { updatingDrives = false; }
            }
            selected = Drives.SelectedItem as DvdDrive;
            readiness = selected != null && results.TryGetValue(selected.Id, out var result) ? result : DiscReadiness.NoWriter;
            DiscStatus.Text = readiness.Detail;
            SetPreparationAvailable(selected != null && inspections.TryGetValue(selected.Id, out var current) ? current : null);
            outputSelection.Apply(readiness);
            UpdateOutput();
            Stage.Text = "Disc checked (not written)";
            Status.Text = readiness.Detail;
            var otherErrors = results.Where(r => r.Key != selected?.Id && r.Value.Detail.StartsWith("Disc detection failed:", StringComparison.Ordinal))
                .Select(r => r.Value.Detail);
            if (otherErrors.Any()) Status.Text += "\n" + string.Join("\n", otherErrors);
        }
        catch (OperationCanceledException) { Stage.Text = "Cancelled"; }
        catch (Exception ex)
        {
            readiness = DiscReadiness.Failed(ex.Message);
            SetPreparationAvailable(null);
            DiscStatus.Text = readiness.Detail;
            outputSelection.Apply(readiness); UpdateOutput();
            Stage.Text = "Disc check failed"; Status.Text = readiness.Detail;
        }
        finally { cancellation.Dispose(); cancellation = null; Progress.IsIndeterminate = false; Busy(false); }
    }
    private void OutputChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || updatingOutput) return;
        outputSelection.Choose(Output.SelectedIndex switch { 1 => OutputKind.Folder, 2 => OutputKind.Burn, _ => OutputKind.Iso });
        UpdateOutput();
    }
    private void UseAutomaticOutput(object sender, RoutedEventArgs e)
    {
        outputSelection.UseAutomatic(readiness);
        UpdateOutput();
    }
    private void SetPreparationAvailable(DiscInspection? inspection)
    {
        bool available = false;
        if (inspection?.NeedsErase == true)
        {
            try { inspection.CheckForPreparation(2048); available = true; }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or OverflowException)
            { System.Diagnostics.Trace.TraceInformation($"Disc preparation unavailable: {ex.Message}"); }
        }
        SourcePrepareDiscButton.IsEnabled = PrepareDiscButton.IsEnabled = available;
    }

    private async Task<bool> PreflightDiscAsync(DvdDrive? drive, long requiredBytes, CancellationToken token)
    {
        if (drive == null) throw new InvalidOperationException("No DVD writer selected. Connect a writer and refresh, or explicitly choose Save DVD-ready ISO.");
        Stage.Text = "Disc preflight";
        Status.Text = $"Checking {drive.Label}: media type, state and capacity. No disc is written or erased by this check.";
        DiscInspection? latest = null;
        DiscPreparationResult result;
        try
        {
            result = await discPreparation.RunAsync(requiredBytes, async ct =>
            {
                ct.ThrowIfCancellationRequested();
                await RunStaAsync(() => latest = DiscWriter.InspectPreparation(drive));
                return latest ?? throw new InvalidOperationException("The writer returned no disc inspection.");
            }, inspection =>
            {
                Stage.Text = "Preparation needs permission";
                return MessageBox.Show(this,
                    $"Prepare / erase {inspection.Media.Name} in {drive.Label}?\n\nALL existing contents on this disc will be removed. This cannot be undone. This is a quick optical-media erase, not a secure data wipe and not Windows / Live File System formatting.\n\nNo DVD will be burned by this permission. Burning requires a separate confirmation. IMAPI erasure cannot be cancelled once it starts.\n\nKeep this same disc in the drive. Erase it now?",
                    "Confirm destructive disc preparation", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
            }, async (inspection, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                erasing = true; CancelButton.IsEnabled = false;
                Stage.Text = "Preparing / erasing disc";
                Status.Text = $"Erasing {inspection.Media.Name} in {drive.Label}. Do not remove the disc. This IMAPI stage cannot be cancelled; blankness and capacity will be checked afterward.";
                Progress.IsIndeterminate = true;
                await RunStaAsync(() => DiscWriter.Erase(drive, inspection, requiredBytes, ct));
            }, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            readiness = DiscReadiness.Failed(ex.Message); DiscStatus.Text = readiness.Detail; SetPreparationAvailable(null);
            throw;
        }
        finally { erasing = false; Progress.IsIndeterminate = false; CancelButton.IsEnabled = !burning; }
        if (result == DiscPreparationResult.Declined)
        {
            Stage.Text = "Preparation declined";
            Status.Text = "Nothing was erased or burned. Keep the existing disc contents, or insert a new blank DVD.";
            return false;
        }
        var checkedDisc = latest ?? throw new InvalidOperationException("The writer returned no disc inspection.");
        readiness = DiscReadiness.Assess(checkedDisc.Media, requiredBytes);
        DiscStatus.Text = result == DiscPreparationResult.ErasedAndReady
            ? $"Prepared and checked: {checkedDisc.Media.Summary}. Burning still requires confirmation."
            : $"Preflight passed: {checkedDisc.Media.Summary}. No formatting or erasure needed.";
        SetPreparationAvailable(checkedDisc);
        Stage.Text = "Disc ready";
        Status.Text = DiscStatus.Text;
        return true;
    }

    private async void PrepareDisc(object sender, RoutedEventArgs e)
    {
        if (cancellation != null) return;
        cancellation = new(); Busy(true);
        try
        {
            long required = RequiredBurnBytes();
            if (WorkspaceTabs.SelectedIndex == 1 && PreparedJobs.SelectedItem is PreparedJob job)
            {
                using var dvd = await PreparedDvd.OpenAsync(job.Path, Reporter(), cancellation.Token);
                required = dvd.Length;
                await PreflightDiscAsync(Drives.SelectedItem as DvdDrive, required, cancellation.Token);
            }
            else await PreflightDiscAsync(Drives.SelectedItem as DvdDrive, required, cancellation.Token);
        }
        catch (OperationCanceledException) { Stage.Text = "Preparation cancelled"; Status.Text = "Cancelled before erasure. No DVD was burned."; }
        catch (Exception ex)
        {
            readiness = DiscReadiness.Failed(ex.Message); DiscStatus.Text = readiness.Detail; SetPreparationAvailable(null);
            Stage.Text = "Disc not ready"; Status.Text = $"Disc preparation did not verify readiness: {ex.Message}\nNo DVD was burned. Check the disc before trying again.";
        }
        finally { cancellation.Dispose(); cancellation = null; erasing = false; Progress.IsIndeterminate = false; Busy(false); }
    }
    private void UpdateOutput()
    {
        updatingOutput = true;
        try { Output.SelectedIndex = outputSelection.Selected switch { OutputKind.Folder => 1, OutputKind.Burn => 2, _ => 0 }; }
        finally { updatingOutput = false; }
        SaveOptions.Visibility = outputSelection.Selected == OutputKind.Burn ? Visibility.Collapsed : Visibility.Visible;
        SavePath.Text = outputSelection.Selected == OutputKind.Folder ? folderDestination : isoDestination;
        OutputChoiceHint.Text = outputSelection.IsManual ? "Your choice is kept until you use automatic." :
            "Automatic: burn if a suitable blank DVD is ready; otherwise save an ISO.";
        UpdatePrimaryAction();
    }
    private void WorkspaceChanged(object sender, SelectionChangedEventArgs e)
    { if (initialized && ReferenceEquals(e.Source, WorkspaceTabs)) UpdatePrimaryAction(); }
    private void UpdatePrimaryAction() => StartButton.Content = WorkspaceTabs.SelectedIndex == 1 ? "_Burn selected DVD…" :
        outputSelection.Selected == OutputKind.Burn ? "_Prepare and burn…" : "_Prepare DVD";
    private bool SelectDestination()
    {
        if (outputSelection.Selected == OutputKind.Folder)
        {
            var dialog = new OpenFolderDialog { Title = "Choose a parent folder for a new DVD folder", InitialDirectory = Path.GetDirectoryName(folderDestination) };
            if (dialog.ShowDialog(this) != true) return false;
            folderDestination = Path.Combine(dialog.FolderName, $"Compressr-DVD-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}");
        }
        else
        {
            var dialog = new SaveFileDialog { Title = "Save DVD-ready ISO", Filter = "DVD image|*.iso", DefaultExt = ".iso",
                AddExtension = true, FileName = Path.GetFileName(isoDestination), InitialDirectory = Path.GetDirectoryName(isoDestination), OverwritePrompt = true };
            if (dialog.ShowDialog(this) != true) return false;
            isoDestination = dialog.FileName;
        }
        UpdateOutput();
        return true;
    }
    private void ChooseDestination(object sender, RoutedEventArgs e)
    {
        try { SelectDestination(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or Win32Exception)
        { Stage.Text = "Save location unavailable"; Status.Text = $"Could not select the DVD save location: {ex.Message}"; }
    }
    private void LoadPreparedJobs()
    {
        try
        {
            string path = Path.Combine(stateDirectory, "prepared-jobs.json");
            if (!File.Exists(path)) return;
            var paths = JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) ?? throw new InvalidDataException("Prepared DVD list is empty or invalid.");
            foreach (string file in paths)
            {
                if (string.IsNullOrWhiteSpace(file) || !Path.IsPathFullyQualified(file))
                    throw new InvalidDataException("Prepared DVD list contains an invalid path.");
                if (!preparedJobs.Any(j => string.Equals(j.Path, file, StringComparison.OrdinalIgnoreCase)))
                    preparedJobs.Add(new(file, "Not checked this run"));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
        { PreparedListNotice.Text = $"Could not load prepared DVD list: {ex.Message}"; }
    }
    private void SavePreparedJobs()
    {
        try
        {
            string path = Path.Combine(stateDirectory, "prepared-jobs.json");
            string pending = path + ".tmp";
            File.WriteAllText(pending, JsonSerializer.Serialize(preparedJobs.Select(j => j.Path)));
            File.Move(pending, path, true);
            PreparedListNotice.Text = "The list is saved between runs. Removing an entry never deletes its file.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { PreparedListNotice.Text = $"Could not save prepared DVD list: {ex.Message} Entries are available only in this run."; }
    }
    private PreparedJob AddPreparedJob(string path, string status)
    {
        path = Path.GetFullPath(path);
        var existing = preparedJobs.FirstOrDefault(j => string.Equals(j.Path, path, StringComparison.OrdinalIgnoreCase));
        var job = new PreparedJob(path, status);
        if (existing == null) preparedJobs.Add(job);
        else preparedJobs[preparedJobs.IndexOf(existing)] = job;
        PreparedJobs.SelectedItem = job;
        SavePreparedJobs();
        return job;
    }
    private void SetPreparedStatus(string path, string status)
    {
        var existing = preparedJobs.FirstOrDefault(j => string.Equals(j.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing == null) return;
        bool selected = Equals(PreparedJobs.SelectedItem, existing);
        var updated = existing with { Status = status };
        preparedJobs[preparedJobs.IndexOf(existing)] = updated;
        if (selected) PreparedJobs.SelectedItem = updated;
    }
    private void PreparedSelected(object sender, SelectionChangedEventArgs e)
    {
        if (PreparedPath == null) return;
        var job = PreparedJobs.SelectedItem as PreparedJob;
        PreparedPath.Text = job == null ? "Add saved ISO files, or prepare one above. Original videos are not needed." : $"{job.Path}\n{job.Status}";
        BurnSelectedButton.IsEnabled = RemovePreparedButton.IsEnabled = job != null;
    }
    private void RemovePrepared(object sender, RoutedEventArgs e)
    {
        int index = PreparedJobs.SelectedIndex;
        if (index < 0 || cancellation != null) return;
        preparedJobs.RemoveAt(index);
        PreparedJobs.SelectedIndex = Math.Min(index, preparedJobs.Count - 1);
        SavePreparedJobs();
        Status.Text = "Removed from the list only. The saved ISO has not been deleted.";
    }
    private async void AddPrepared(object sender, RoutedEventArgs e)
    {
        if (cancellation != null) return;
        var dialog = new OpenFileDialog { Title = "Add prepared DVD ISOs (one file per disc)", Filter = "DVD-ready ISO|*.iso", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        await AddPreparedPathsAsync(dialog.FileNames);
    }
    private async Task AddPreparedPathsAsync(IEnumerable<string> paths)
    {
        if (cancellation != null) return;
        cancellation = new(); Busy(true);
        int valid = 0, invalid = 0;
        try
        {
            foreach (string path in paths)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                AddPreparedJob(path, "Checking ISO…");
                try
                {
                    using var dvd = await PreparedDvd.OpenAsync(path, Reporter(), cancellation.Token);
                    SetPreparedStatus(path, $"Verified ISO · {dvd.Length / 1_000_000.0:F1} MB · not written");
                    valid++;
                }
                catch (OperationCanceledException) { SetPreparedStatus(path, "Check cancelled; rechecked before burning"); throw; }
                catch (Exception ex) { SetPreparedStatus(path, $"Invalid / unavailable: {ex.Message}"); invalid++; }
            }
            Stage.Text = "Prepared files checked";
            Status.Text = $"{valid} valid ISO(s), {invalid} invalid or unavailable. Select one prepared DVD to burn to one blank disc. Files are never joined.";
        }
        catch (OperationCanceledException) { Stage.Text = "Cancelled"; Status.Text = "ISO checks cancelled. Nothing was written."; }
        finally { cancellation.Dispose(); cancellation = null; Progress.IsIndeterminate = false; Busy(false); }
    }
    private void Browse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Filter = "Video files|*.mp4;*.mkv;*.mov;*.avi;*.wmv;*.webm;*.mpeg;*.mpg;*.m4v;*.mts;*.m2ts|All files|*.*" };
        if (dialog.ShowDialog(this) == true)
            foreach (string file in dialog.FileNames) AddVideo(file);
    }
    private void SourceChanged(object sender, TextChangedEventArgs e)
    { if (initialized && Source.Text.Length > 0) Estimate.Text = "Input changed. Add it, then Check fit for the combined duration."; }
    private void AddVideo(string input)
    {
        var item = new SourceVideo(input);
        videos.Add(item);
        Inputs.SelectedIndex = videos.Count - 1;
        Estimate.Text = "List changed. Check fit before making the DVD.";
        if (!item.IsLocal) _ = LoadTitleAsync(item);
    }
    private async Task<YouTubeInfo> LookupMetadataAsync(string input, CancellationToken token)
    {
        await titleSlots.WaitAsync(token);
        try { return await Tools().YouTubeMetadataAsync(input, token); }
        finally { titleSlots.Release(); }
    }
    private async Task LoadTitleAsync(SourceVideo item)
    {
        var pending = item.MetadataAsync(metadataLookup, CancellationToken.None);
        UpdateCancelButton();
        try { await pending; }
        catch (OperationCanceledException) { /* The queue row reports cancellation or timeout. */ }
        catch (Exception) { /* The queue row retains the lookup error and original URL. */ }
        finally { UpdateCancelButton(); }
    }
    private async void RetryTitle(object sender, RoutedEventArgs e)
    {
        if (cancellation == null && Inputs.SelectedItem is SourceVideo item && !item.IsLocal)
            await LoadTitleAsync(item);
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
        rotation = (Inputs.SelectedItem as SourceVideo)?.Rotation ?? 0;
        if (Preview != null) Preview.Source = null;
        if (RotationLabel != null) RotationLabel.Text = $"Extra rotation: {rotation}°";
    }
    private void MoveUp(object sender, RoutedEventArgs e) => MoveInput(-1);
    private void MoveDown(object sender, RoutedEventArgs e) => MoveInput(1);
    private void MoveInput(int delta)
    {
        int index = Inputs.SelectedIndex;
        if (index < 0 || index + delta < 0 || index + delta >= videos.Count || cancellation != null) return;
        ReorderInput(videos[index], index + delta > index ? index + delta + 1 : index + delta);
    }
    private void ReorderInput(SourceVideo item, int insertion)
    {
        if (cancellation != null || !videos.Contains(item)) return;
        int previous = videos.IndexOf(item);
        int destination = Math.Clamp(insertion, 0, videos.Count);
        if (destination > previous) destination--;
        videos.Move(previous, destination);
        Inputs.SelectedItem = item;
        Inputs.ScrollIntoView(item);
        if (Inputs.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container) container.Focus();
        Estimate.Text = "Play order changed. Check fit before making the DVD.";
    }
    private void QueueKeyDown(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Keyboard.Modifiers != ModifierKeys.Alt || key is not (Key.Up or Key.Down)) return;
        MoveInput(key == Key.Up ? -1 : 1);
        e.Handled = true;
    }
    private void QueueMouseDown(object sender, MouseButtonEventArgs e)
    {
        dragOrigin = e.GetPosition(Inputs);
        draggedVideo = (ItemsControl.ContainerFromElement(Inputs, e.OriginalSource as DependencyObject) as ListBoxItem)?.DataContext as SourceVideo;
    }
    private void QueueMouseMove(object sender, MouseEventArgs e)
    {
        if (cancellation != null || draggedVideo == null || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(Inputs);
        if (Math.Abs(point.X - dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var item = draggedVideo;
        draggedVideo = null;
        try { DragDrop.DoDragDrop(Inputs, new DataObject(typeof(SourceVideo), item), DragDropEffects.Move); }
        finally { ClearDropMarker(); }
    }
    private int QueueInsertion(Point point, SourceVideo dragged, out double markerY)
    {
        markerY = 2;
        for (int i = 0; i < videos.Count; i++)
        {
            if (Inputs.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem row) continue;
            double top = row.TranslatePoint(new Point(), Inputs).Y;
            if (point.Y < top) { markerY = top; return i; }
            if (point.Y <= top + row.ActualHeight)
            {
                bool after = i >= videos.IndexOf(dragged);
                markerY = after ? top + row.ActualHeight : top;
                return after ? i + 1 : i;
            }
            markerY = top + row.ActualHeight;
        }
        return videos.Count;
    }
    private void ClearDropMarker()
    {
        if (dropMarker == null) return;
        AdornerLayer.GetAdornerLayer(Inputs)?.Remove(dropMarker);
        dropMarker = null;
    }
    private void QueueDragLeave(object sender, DragEventArgs e) => ClearDropMarker();

    private sealed class QueueDropMarker(UIElement owner) : Adorner(owner)
    {
        public double Y { get; set; }
        protected override void OnRender(DrawingContext drawingContext) =>
            drawingContext.DrawLine(new Pen((Brush)FindResource("Accent"), 3), new(2, Y), new(ActualWidth - 2, Y));
    }
    private void RemoveInput(object sender, RoutedEventArgs e)
    {
        int index = Inputs.SelectedIndex;
        if (index < 0) return;
        videos[index].CancelTitle();
        videos.RemoveAt(index); Inputs.SelectedIndex = Math.Min(index, videos.Count - 1);
        Estimate.Text = "List changed. Check fit for the updated duration.";
    }
    private IReadOnlyList<string> DroppedSources(IDataObject data)
    {
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
        {
            if (files.Length == 0 || files.Any(file => !File.Exists(file)))
                throw new ArgumentException("Drop existing video files, not folders or missing files.");
            return files;
        }
        foreach (string format in new[] { "UniformResourceLocatorW", "UniformResourceLocator", "text/uri-list", DataFormats.UnicodeText, DataFormats.Text })
        {
            if (!data.GetDataPresent(format)) continue;
            object? value = data.GetData(format);
            string? text = value as string;
            if (value is MemoryStream stream)
            {
                var bytes = stream.ToArray();
                text = (format.EndsWith('W') ? System.Text.Encoding.Unicode : System.Text.Encoding.UTF8).GetString(bytes);
            }
            if (!string.IsNullOrWhiteSpace(text)) return SourceDrop.ParseLinks(text);
        }
        throw new ArgumentException("Drop video files or HTTPS YouTube video links.");
    }
    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        if (cancellation != null || WorkspaceTabs.SelectedIndex != 0) { ClearDropMarker(); return; }
        if (e.Data.GetDataPresent(typeof(SourceVideo)))
        {
            var point = e.GetPosition(Inputs);
            if (point.X < 0 || point.X > Inputs.ActualWidth || point.Y < 0 || point.Y > Inputs.ActualHeight)
            { ClearDropMarker(); return; }
            if (e.Data.GetData(typeof(SourceVideo)) is not SourceVideo item || !videos.Contains(item)) return;
            e.Effects = DragDropEffects.Move;
            if (Environment.TickCount64 - lastDragScroll > 120 && FindChild<ScrollViewer>(Inputs) is { } scroll)
            {
                if (point.Y < 28) scroll.LineUp();
                else if (point.Y > Inputs.ActualHeight - 28) scroll.LineDown();
                lastDragScroll = Environment.TickCount64;
            }
            QueueInsertion(point, item, out double y);
            if (dropMarker == null && AdornerLayer.GetAdornerLayer(Inputs) is { } layer)
            {
                dropMarker = new(Inputs) { IsHitTestVisible = false };
                layer.Add(dropMarker);
            }
            if (dropMarker != null) { dropMarker.Y = y; dropMarker.InvalidateVisual(); }
            return;
        }
        ClearDropMarker();
        try { _ = DroppedSources(e.Data); e.Effects = DragDropEffects.Copy; }
        catch (ArgumentException) { /* Drop reports the validation error; drag only advertises supported data. */ }
    }
    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        ClearDropMarker();
        if (cancellation != null || WorkspaceTabs.SelectedIndex != 0) { e.Effects = DragDropEffects.None; return; }
        if (e.Data.GetDataPresent(typeof(SourceVideo)))
        {
            var point = e.GetPosition(Inputs);
            if (point.X >= 0 && point.X <= Inputs.ActualWidth && point.Y >= 0 && point.Y <= Inputs.ActualHeight &&
                e.Data.GetData(typeof(SourceVideo)) is SourceVideo item)
                ReorderInput(item, QueueInsertion(point, item, out _));
            return;
        }
        try
        {
            foreach (string input in DroppedSources(e.Data)) AddVideo(input);
            Status.Text = "Sources added in drop order. Drag clips to change DVD playback order.";
        }
        catch (ArgumentException ex) { e.Effects = DragDropEffects.None; Status.Text = ex.Message; }
    }
    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (FindChild<T>(child) is { } descendant) return descendant;
        }
        return null;
    }
    private IProgress<StageProgress> Reporter() => new Progress<StageProgress>(p =>
    { Stage.Text = p.Stage; Status.Text = p.Detail; Progress.IsIndeterminate = p.Fraction == null; Progress.Value = (p.Fraction ?? 0) * 100; });
    private async Task<string> ResolveAsync(SourceVideo item, MediaTools tools, IProgress<StageProgress> progress, CancellationToken token)
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
                var metadata = await item.MetadataAsync(metadataLookup, token);
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
        StartButton.IsEnabled = !value;
        CancelButton.IsEnabled = !burning && !erasing && (value || videos.Any(v => v.IsResolvingTitle));
    }
    private void UpdateCancelButton() =>
        CancelButton.IsEnabled = !burning && !erasing && (cancellation != null || videos.Any(v => v.IsResolvingTitle));
    private async void ShowPreview(object sender, RoutedEventArgs e) => await PreviewAsync();
    private async void Rotate(object sender, RoutedEventArgs e)
    {
        if (!AddPending()) return;
        rotation = (rotation + 90) % 360;
        ((SourceVideo)Inputs.SelectedItem).Rotation = rotation;
        RotationLabel.Text = $"Extra rotation: {rotation}°"; await PreviewAsync();
    }
    private async Task PreviewAsync()
    {
        if (cancellation != null || !AddPending()) return;
        cancellation = new(); Busy(true);
        try
        {
            var tools = Tools(); var reporter = Reporter();
            var selected = (SourceVideo)Inputs.SelectedItem;
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
        if (WorkspaceTabs.SelectedIndex == 1) { BurnPrepared(sender, e); return; }
        if (cancellation != null || !AddPending()) return;
        OutputKind output = outputSelection.Selected;
        DvdDrive? drive = Drives.SelectedItem as DvdDrive;
        string destination;
        long? originalLength;
        DateTime? originalModified;
        try
        {
            if (output == OutputKind.Iso && File.Exists(isoDestination) && !SelectDestination()) return;
            destination = output == OutputKind.Folder ? folderDestination : isoDestination;
            var existing = output == OutputKind.Iso && File.Exists(destination) ? new FileInfo(destination) : null;
            originalLength = existing?.Length;
            originalModified = existing?.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or Win32Exception)
        { Stage.Text = "Save location unavailable"; Status.Text = $"Could not use the DVD save location: {ex.Message}"; return; }
        cancellation = new(); Busy(true);
        string work = Path.Combine(stateDirectory, "jobs", Guid.NewGuid().ToString("N"));
        if (output == OutputKind.Burn) destination = Path.Combine(work, "prepared.iso");
        string? partialIso = null;
        try
        {
            var token = cancellation.Token; var reporter = Reporter(); var tools = Tools();
            var estimate = await EstimateAsync(tools, token);
            _ = DvdPlan.Create(estimate, (TelevisionSystem)Standard.SelectedItem, (DiscSize)Capacity.SelectedItem, 0);
            if (output == OutputKind.Burn)
            {
                if (drive == null) throw new InvalidOperationException("No DVD writer selected. Connect a writer and refresh, or explicitly choose Save DVD-ready ISO.");
                if (!await PreflightDiscAsync(drive, RequiredBurnBytes(), token)) return;
                token.ThrowIfCancellationRequested();
            }
            if (output == OutputKind.Folder && Directory.Exists(destination))
                throw new IOException("The DVD output folder already exists. Change the save location to create a new DVD folder.");
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
            if (output == OutputKind.Folder)
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
                if (originalLength != null)
                {
                    var current = new FileInfo(destination);
                    if (!current.Exists || current.Length != originalLength || current.LastWriteTimeUtc != originalModified)
                        throw new IOException("The destination ISO changed during preparation. It was not overwritten. Choose another filename and prepare again.");
                }
                File.Move(partialIso, destination, originalLength != null); partialIso = null;
                AddPreparedJob(destination, "Verified ISO · not written");
                if (output == OutputKind.Burn)
                {
                    await BurnIsoAsync(destination, drive, token);
                    return;
                }
            }
            Stage.Text = "Done"; Progress.IsIndeterminate = false; Progress.Value = 100;
            Status.Text = $"Saved to {destination}\n" + (output == OutputKind.Iso
                ? "DVD-ready ISO verified and added to Burn prepared DVDs. Keep this file; select it in that tab whenever you have a blank disc. Original videos and work folders are not needed."
                : "DVD folder saved. Choose ISO output for the prepare-now / burn-later workflow.");
        }
        catch (OperationCanceledException)
        {
            Stage.Text = "Cancelled";
            Status.Text = $"No disc was written. Work files retained at {work}. Output choice unchanged." +
                (output == OutputKind.Folder ? $"\nAny partially saved folder at {destination} is not a complete DVD." : "");
            SetPreparedStatus(destination, "Cancelled · not written");
        }
        catch (Exception ex)
        {
            Stage.Text = burning ? "Disc not verified" : "Could not complete DVD";
            Status.Text = $"{ex.Message}\nWork files retained at {work}. Output choice unchanged; no automatic file fallback." +
                (output == OutputKind.Folder ? $"\nAny partially saved folder at {destination} is not a complete DVD." : "");
            SetPreparedStatus(destination, burning ? "Write / verification failed; disc not verified" : $"Not written: {ex.Message}");
        }
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
        if (PreparedJobs.SelectedItem is not PreparedJob job) { Status.Text = "Select one prepared ISO first."; return; }
        cancellation = new(); Busy(true);
        try
        {
            await BurnIsoAsync(job.Path, Drives.SelectedItem as DvdDrive, cancellation.Token);
        }
        catch (OperationCanceledException) { Stage.Text = "Cancelled"; Status.Text = "No disc was written. The prepared ISO is unchanged."; SetPreparedStatus(job.Path, "Cancelled · not written"); }
        catch (Exception ex)
        {
            Stage.Text = burning ? "Disc not verified" : "Could not burn prepared DVD"; Status.Text = ex.Message;
            SetPreparedStatus(job.Path, burning ? $"Disc not verified: {ex.Message}" : $"Not written: {ex.Message}");
        }
        finally { burning = false; cancellation.Dispose(); cancellation = null; Progress.IsIndeterminate = false; Busy(false); }
    }
    private async Task BurnIsoAsync(string path, DvdDrive? drive, CancellationToken token)
    {
        var reporter = Reporter();
        SetPreparedStatus(path, "Checking ISO and disc…");
        using var prepared = await PreparedDvd.OpenAsync(path, reporter, token);
        token.ThrowIfCancellationRequested();
        if (drive == null) throw new InvalidOperationException("No DVD writer selected. Connect a writer, refresh, then select this ISO again. The saved ISO is unchanged.");
        if (!await PreflightDiscAsync(drive, prepared.Length, token))
        { SetPreparedStatus(path, "Preparation declined · not written"); return; }
        token.ThrowIfCancellationRequested();
        if (MessageBox.Show(this, $"Write and finalise {Path.GetFileName(prepared.Path)} ({prepared.Length / 1_000_000.0:F1} MB) to {drive.Label}?\nOne ISO will be written to one disc. This cannot be undone. Disc preflight has passed; this write will not erase existing contents.",
            "Confirm DVD burning", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            Stage.Text = "Not written"; Status.Text = $"Burn declined. The prepared ISO is unchanged at {path}. Output choice unchanged.";
            SetPreparedStatus(path, "Burn declined · not written"); return;
        }
        token.ThrowIfCancellationRequested();
        burning = true; CancelButton.IsEnabled = false;
        SetPreparedStatus(path, "Writing / verifying · cannot cancel");
        await RunStaAsync(() => DiscWriter.Burn(prepared, drive, reporter));
        await DiscWriter.VerifyAsync(prepared, drive, reporter);
        Stage.Text = "Done"; Progress.Value = 100;
        Status.Text = "One DVD burned, finalised and verified by SHA-256 read-back. Insert a new blank disc, then select another prepared ISO to repeat.";
        SetPreparedStatus(path, "Burned and verified this run");
        readiness = new(false, "Disc has been written. Insert a new blank DVD and check disc before the next job.");
        DiscStatus.Text = readiness.Detail;
    }
    private static Task RunStaAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); completion.SetResult(); } catch (Exception ex) { completion.SetException(ex); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
    }
    private void Cancel(object sender, RoutedEventArgs e)
    {
        if (burning || erasing) return;
        cancellation?.Cancel();
        foreach (var item in videos) item.CancelTitle();
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (cancellation != null) { e.Cancel = true; Status.Text = erasing ? "Wait until disc erasure and readiness verification finish." : burning ? "Wait until disc writing and verification finish." : "Cancel the current operation before closing."; return; }
        foreach (var item in videos) item.CancelTitle();
        foreach (string file in previewFiles)
        {
            try { File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { System.Diagnostics.Trace.TraceWarning($"Could not remove preview {file}: {ex.Message}"); }
        }
    }
}
