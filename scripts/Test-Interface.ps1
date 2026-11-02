param([string]$ScreenshotDirectory, [string[]]$PreparedIsoPaths, [string[]]$SourceVideoPaths)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Core' -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    throw 'Run with PowerShell 7 on Windows in STA mode: pwsh -STA -File scripts\Test-Interface.ps1'
}
$root = Split-Path $PSScriptRoot -Parent
$previousTools = $env:COMPRESSR_TOOLS
if (!$env:COMPRESSR_TOOLS) { $env:COMPRESSR_TOOLS = Join-Path $root 'tools' }
Add-Type -AssemblyName PresentationFramework
Add-Type -Path (Join-Path $root 'Compressr.Core\bin\Release\net8.0\Compressr.Core.dll')
Add-Type -Path (Join-Path $root 'Compressr.App\bin\Release\net8.0-windows\Compressr.dll')
Add-Type -CompilerOptions '/nowarn:1701' -ReferencedAssemblies @(
    (Join-Path $root 'Compressr.Core\bin\Release\net8.0\Compressr.Core.dll'),
    (Join-Path $root 'Compressr.App\bin\Release\net8.0-windows\Compressr.dll'),
    'System.Runtime', 'System.Collections') -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Compressr.App;
using Compressr.Core;
public static class DiscMonitorFixture
{
    public static DvdDrive[] Drives = Array.Empty<DvdDrive>();
    public static DiscInspection Snapshot;
    public static int Inspections;
    public static string Failure;
    public static string StateDirectory;
    public static string WriteBehavior = "fail";
    public static string VerifyBehavior = "pass";
    public static int Writes;
    public static int Verifications;
    public static bool RegisteredBeforeWrite;
    public static Func<IReadOnlyList<DvdDrive>> Enumerate => () => Drives;
    public static Func<DvdDrive, DiscInspection> Inspect => drive =>
    {
        Inspections++;
        if (!string.IsNullOrEmpty(Failure) || Snapshot == null) throw new InvalidOperationException(
            string.IsNullOrEmpty(Failure) ? "Unavailable test recorder" : Failure);
        return Snapshot;
    };
    public static Task Write(PreparedDvd dvd, DvdDrive drive, IProgress<StageProgress> progress, CancellationToken token)
    {
        Writes++;
        string catalog = System.IO.Path.Combine(StateDirectory, "prepared-jobs.json");
        string paths = File.ReadAllText(catalog);
        string persistedPath = System.IO.Path.GetFullPath(dvd.Path).Replace("\\", "\\\\");
        RegisteredBeforeWrite = File.Exists(dvd.Path) && paths.Contains(persistedPath, StringComparison.OrdinalIgnoreCase);
        return WriteBehavior switch
        {
            "cancel" => Task.FromCanceled(new CancellationToken(true)),
            "pass" => Task.CompletedTask,
            _ => Task.FromException(new IOException("Simulated IMAPI write failure (test only)."))
        };
    }
    public static Task Verify(PreparedDvd dvd, DvdDrive drive, IProgress<StageProgress> progress, CancellationToken token)
    {
        Verifications++;
        return VerifyBehavior == "crc"
            ? Task.FromException(new IOException("Simulated read-back CRC failure (test only)."))
            : Task.CompletedTask;
    }
    public static bool Confirm(PreparedDvd dvd, DvdDrive drive) => true;
}
public sealed class BurnEventFixture
{
    public int CurrentAction { get; set; } = 4;
    public int StartLba => 100;
    public int SectorCount => 1000;
    public int LastWrittenLba => 599;
    public int ElapsedTime => 60;
    public int RemainingTime => 90;
    public int TotalTime => 150;
}
public sealed class ProgressCollector : IProgress<StageProgress>
{
    public StageProgress Latest;
    public void Report(StageProgress value) => Latest = value;
}
'@
$app = [Compressr.App.App]::new()
$app.InitializeComponent()
[Threading.SynchronizationContext]::SetSynchronizationContext(
    [Windows.Threading.DispatcherSynchronizationContext]::new([Windows.Threading.Dispatcher]::CurrentDispatcher))
$testState = Join-Path $root ("artifacts\interface-state-" + [Guid]::NewGuid().ToString('N'))
$titleRequests = [Collections.Generic.Dictionary[string, Threading.Tasks.TaskCompletionSource[Compressr.Core.YouTubeInfo]]]::new()
$titleLookup = [Func[string, Threading.CancellationToken, Threading.Tasks.Task[Compressr.Core.YouTubeInfo]]]{
    param($url, $token)
    $pending = [Threading.Tasks.TaskCompletionSource[Compressr.Core.YouTubeInfo]]::new()
    $titleRequests[$url] = $pending
    return $pending.Task.WaitAsync($token)
}
$constructor = [Compressr.App.MainWindow].GetConstructor([Reflection.BindingFlags]'Instance,NonPublic',
    $null, [Type[]]@([string], $titleLookup.GetType(), [DiscMonitorFixture]::Enumerate.GetType(), [DiscMonitorFixture]::Inspect.GetType()), $null)
$window = $constructor.Invoke([object[]]@([string]$testState, $titleLookup, [DiscMonitorFixture]::Enumerate, [DiscMonitorFixture]::Inspect))
$ui = @{}
$script:checks = 0
function Check([bool]$condition, [string]$message) {
    if (!$condition) { throw "FAIL: $message" }
    $script:checks++
}
function Invoke-Handler([string]$name, [object[]]$arguments) {
    $method = $window.GetType().GetMethod($name, [Reflection.BindingFlags]'Instance,NonPublic,DeclaredOnly')
    $method.Invoke($window, $arguments)
}
function Drop-Data([Windows.IDataObject]$data) {
    $ctor = [Windows.DragEventArgs].GetConstructors([Reflection.BindingFlags]'Instance,NonPublic')[0]
    $event = $ctor.Invoke([object[]]@($data, [Windows.DragDropKeyStates]::None, [Windows.DragDropEffects]::Copy,
        $ui.Source, [Windows.Point]::new(0, 0)))
    $event.RoutedEvent = [Windows.DragDrop]::PreviewDropEvent
    $ui.Source.RaiseEvent($event)
    return $event
}
function Queue-Drag([Windows.IDataObject]$data, [double]$y, [bool]$drop) {
    $ctor = [Windows.DragEventArgs].GetConstructors([Reflection.BindingFlags]'Instance,NonPublic')[0]
    $event = $ctor.Invoke([object[]]@($data, [Windows.DragDropKeyStates]::LeftMouseButton,
        [Windows.DragDropEffects]::Move, $ui.Inputs, [Windows.Point]::new(10, $y)))
    $event.RoutedEvent = if ($drop) { [Windows.DragDrop]::PreviewDropEvent } else { [Windows.DragDrop]::PreviewDragOverEvent }
    $ui.Inputs.RaiseEvent($event)
    return $event
}
function Await-Task([Threading.Tasks.Task]$task) {
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while (!$task.IsCompleted) {
        if ([DateTime]::UtcNow -gt $deadline) { throw 'UI task timed out.' }
        [Windows.Threading.Dispatcher]::CurrentDispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::Background)
        Start-Sleep -Milliseconds 10
    }
    $null = $task.GetAwaiter().GetResult()
}
function Await-Idle {
    $field = $window.GetType().GetField('cancellation', [Reflection.BindingFlags]'Instance,NonPublic')
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while ($null -ne $field.GetValue($window)) {
        if ([DateTime]::UtcNow -gt $deadline) { throw 'UI operation timed out.' }
        [Windows.Threading.Dispatcher]::CurrentDispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::Background)
        Start-Sleep -Milliseconds 10
    }
}
function Await-Estimate {
    $field = $window.GetType().GetField('estimateRefresh', [Reflection.BindingFlags]'Instance,NonPublic')
    do {
        $task = $field.GetValue($window)
        if ($task) { Await-Task $task }
    } while ($task -ne $field.GetValue($window))
}
function Layout([double]$width, [double]$height) {
    $window.Content.Measure([Windows.Size]::new($width, $height))
    $window.Content.Arrange([Windows.Rect]::new(0, 0, $width, $height))
    $window.Content.UpdateLayout()
}
function Screenshot([string]$name) {
    if (!$ScreenshotDirectory) { return }
    $content = $window.Content
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new(
        [int]$content.ActualWidth, [int]$content.ActualHeight, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($content)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.File]::Create((Join-Path $ScreenshotDirectory $name))
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
}
function Luminance([string]$hex) {
    $values = @(1, 3, 5 | ForEach-Object {
        $c = [Convert]::ToInt32($hex.Substring($_, 2), 16) / 255.0
        if ($c -le .04045) { $c / 12.92 } else { [Math]::Pow(($c + .055) / 1.055, 2.4) }
    })
    return .2126 * $values[0] + .7152 * $values[1] + .0722 * $values[2]
}
try {
    if ($ScreenshotDirectory) { [IO.Directory]::CreateDirectory($ScreenshotDirectory) | Out-Null }
    $invalidSettingsState = Join-Path $testState 'invalid-settings'
    [IO.Directory]::CreateDirectory($invalidSettingsState) | Out-Null
    [IO.File]::WriteAllText((Join-Path $invalidSettingsState 'dvd-destinations.json'),
        '{"Iso":"relative.iso","Folder":"relative-folder"}')
    $invalidSettingsWindow = $constructor.Invoke([object[]]@([string]$invalidSettingsState, $titleLookup,
        [DiscMonitorFixture]::Enumerate, [DiscMonitorFixture]::Inspect))
    try {
        Check ($invalidSettingsWindow.FindName('Status').Text.Contains('Could not read saved DVD destinations')) 'Invalid persisted destinations are reported without preventing app startup'
    } finally { $invalidSettingsWindow.Close() }
    $namingState = Join-Path $testState 'automatic-names'
    [IO.Directory]::CreateDirectory($namingState) | Out-Null
    $namingFolder = Join-Path $namingState 'output'
    [IO.Directory]::CreateDirectory($namingFolder) | Out-Null
    [IO.File]::WriteAllText((Join-Path $namingState 'dvd-destinations.json'),
        (@{ Iso = (Join-Path $namingFolder 'Compressr.iso'); Folder = (Join-Path $namingState 'dvd') } | ConvertTo-Json))
    $namingWindow = $constructor.Invoke([object[]]@([string]$namingState, $titleLookup,
        [DiscMonitorFixture]::Enumerate, [DiscMonitorFixture]::Inspect))
    $priorWindow = $window
    try {
        $window = $namingWindow
        $firstInput = Join-Path $namingState 'Summer holiday.mp4'
        Invoke-Handler 'AddVideo' @([string]$firstInput)
        $namingPath = $window.FindName('SavePath')
        Check ($namingPath.Text -eq (Join-Path $namingFolder 'Summer holiday.iso')) 'Legacy generic name becomes source-based while preserving the chosen folder'
        Invoke-Handler 'AddVideo' @('https://youtu.be/naming-fixture')
        $namingJobs = $window.FindName('Inputs')
        $namedVideo = $namingJobs.SelectedItem
        $titleRequests[$namedVideo.Input].SetResult([Compressr.Core.YouTubeInfo]::new('Evening performance', 30))
        Await-Task ($namedVideo.MetadataAsync($titleLookup, [Threading.CancellationToken]::None))
        Check ($namingPath.Text.EndsWith('Summer holiday and 1 more.iso')) 'Multiple inputs use first source plus count without changing play order'
        Invoke-Handler 'MoveUp' @($null, [Windows.RoutedEventArgs]::new())
        Check ($namingPath.Text.EndsWith('Evening performance and 1 more.iso')) 'Reorder picks the new first title for an automatic ISO name'
        Invoke-Handler 'RemoveInput' @($null, [Windows.RoutedEventArgs]::new())
        Invoke-Handler 'AddVideo' @('https://youtu.be/first-title-fixture')
        $titleVideo = $namingJobs.SelectedItem
        Invoke-Handler 'MoveUp' @($null, [Windows.RoutedEventArgs]::new())
        Check ($namingPath.Text.EndsWith('YouTube video and 1 more.iso')) 'Pending first title has a safe visible name'
        $titleRequests[$titleVideo.Input].SetResult([Compressr.Core.YouTubeInfo]::new('Resolved title', 30))
        Await-Task ($titleVideo.MetadataAsync($titleLookup, [Threading.CancellationToken]::None))
        Check ($namingPath.Text.EndsWith('Resolved title and 1 more.iso')) 'Metadata arrival refreshes automatic destination without a video download'
        Invoke-Handler 'SetFolderDestination' @([string](Join-Path $namingState 'dvd'))
        $automaticRestored = $constructor.Invoke([object[]]@([string]$namingState, $titleLookup,
            [DiscMonitorFixture]::Enumerate, [DiscMonitorFixture]::Inspect))
        try {
            $window = $automaticRestored
            Invoke-Handler 'AddVideo' @([string](Join-Path $namingState 'New session.mp4'))
            Check ($window.FindName('SavePath').Text -eq (Join-Path $namingFolder 'New session.iso')) 'Automatic filename mode and selected folder persist across restart'
        } finally { $automaticRestored.Close(); $window = $namingWindow }
        $customIso = Join-Path $namingFolder 'My own disc.iso'
        Invoke-Handler 'SetIsoDestination' @([string]$customIso)
        Invoke-Handler 'AddVideo' @([string](Join-Path $namingState 'Another.mp4'))
        Check ($namingPath.Text -eq $customIso) 'Explicit custom filename is never overwritten by automatic naming'
        $namingRestored = $constructor.Invoke([object[]]@([string]$namingState, $titleLookup,
            [DiscMonitorFixture]::Enumerate, [DiscMonitorFixture]::Inspect))
        try {
            $window = $namingRestored
            Invoke-Handler 'AddVideo' @([string]$firstInput)
            Check ($window.FindName('SavePath').Text -eq $customIso) 'Explicit custom filename persists across restart and source changes'
        } finally { $namingRestored.Close(); $window = $namingWindow }
    } finally { $window = $priorWindow; $namingWindow.Close() }
    $format = [Activator]::CreateInstance([Type]::GetTypeFromProgID('IMAPI2.MsftDiscFormat2Data'))
    $eventsType = [Compressr.App.MainWindow].Assembly.GetType('Compressr.App.ImapiBurnProgress')
    $collector = [ProgressCollector]::new()
    $events = [Activator]::CreateInstance($eventsType, [object[]]@($format, $collector))
    try {
        $eventsType.GetMethod('Update').Invoke($events, [object[]]@($format, [BurnEventFixture]::new()))
        Check ($collector.Latest.Fraction -eq 0.5 -and $collector.Latest.Detail.Contains('1:30 remaining')) 'Real IMAPI connection point subscribes without a disc write; event bridge renders typed sector/ETA fixture'
        $badEvent = [object]::new()
        $eventsType.GetMethod('Update').Invoke($events, [object[]]@($format, $badEvent))
        Check ($collector.Latest.Fraction -eq $null -and $collector.Latest.Detail.Contains('unavailable')) 'Unreadable progress event is explicit indeterminate progress, never success'
    } finally { $events.Dispose(); [Runtime.InteropServices.Marshal]::FinalReleaseComObject($format) | Out-Null }
    foreach ($name in @('Source', 'Inputs', 'Estimate', 'Preview', 'RotationLabel', 'Standard', 'Capacity',
        'Output', 'Drives', 'Options', 'BurnOptions', 'StartButton', 'CancelButton', 'Stage', 'Progress', 'Status',
        'WorkspaceTabs', 'SavePath', 'SaveOptions', 'OutputChoiceHint', 'SaveLabel', 'DestinationHint', 'PreparedJobs', 'PreparedPath', 'DiscStatus',
        'BurnSelectedButton', 'RemovePreparedButton', 'CapacityMeter', 'CapacitySummary', 'RetryTitleButton', 'OrientationOptions')) {
        Check ($null -ne $window.FindName($name)) "Retained $name"
        $ui[$name] = $window.FindName($name)
    }
    $ui.QueueSummary = $window.FindName('QueueSummary')
    [xml]$xaml = Get-Content (Join-Path $root 'Compressr.App\MainWindow.xaml') -Raw
    foreach ($attribute in $xaml.SelectNodes('//@*')) {
        if ($attribute.LocalName -in @('Click', 'Drop', 'DragOver', 'PreviewDrop', 'PreviewDragOver', 'DragLeave',
            'PreviewMouseLeftButtonDown', 'PreviewMouseMove', 'PreviewKeyDown', 'SizeChanged', 'Closing', 'SelectionChanged', 'TextChanged')) {
            Check ($null -ne $window.GetType().GetMethod($attribute.Value, [Reflection.BindingFlags]'Instance,NonPublic,DeclaredOnly')) "Handler $($attribute.Value)"
        }
    }
    Check ($window.FontSize -eq 14) 'Readable 14 DIP body typography'
    Check ($null -ne $window.Icon -and $window.Icon.Width -ge 16) 'Window/taskbar uses original embedded Compressr icon'
    $iconBytes = [IO.File]::ReadAllBytes((Join-Path $root 'Compressr.App\Assets\Compressr.ico'))
    Check ([BitConverter]::ToUInt16($iconBytes, 0) -eq 0 -and [BitConverter]::ToUInt16($iconBytes, 2) -eq 1 -and
        [BitConverter]::ToUInt16($iconBytes, 4) -eq 9) 'Multi-resolution ICO has nine valid native entries'
    $decoder = [Windows.Media.Imaging.IconBitmapDecoder]::new([Uri]::new((Join-Path $root 'Compressr.App\Assets\Compressr.ico')),
        [Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat, [Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
    Check (($decoder.Frames | ForEach-Object PixelWidth | Sort-Object) -join ',' -eq '16,20,24,32,40,48,64,128,256') 'Native decoder reads all 16 through 256 px icon frames'
    Check ($null -eq $window.GetType().GetMethod('SaveScreenshot', [Reflection.BindingFlags]'Instance,NonPublic')) 'User-facing screenshot functionality removed'
    foreach ($removed in @('CheckFit', 'CheckDisc', 'PrepareDisc', 'RefreshDrives')) {
        Check ($null -eq $window.GetType().GetMethod($removed, [Reflection.BindingFlags]'Instance,NonPublic')) "Manual $removed action removed; orchestration uses Make DVD"
    }
    Check ($ui.Capacity.SelectedItem -eq [Compressr.Core.DiscSize]::SingleLayer) 'Unchanged single-layer default'
    Check ($ui.Output.SelectedIndex -eq 0) 'Unchanged ISO default'
    $ui.Capacity.SelectedItem = [Compressr.Core.DiscSize]::DualLayer
    Await-Idle
    Check ($ui.Capacity.SelectedItem -eq [Compressr.Core.DiscSize]::DualLayer) 'Dual-layer option remains selectable'
    $ui.Capacity.SelectedItem = [Compressr.Core.DiscSize]::SingleLayer
    Await-Idle
    $ui.Output.SelectedIndex = 1
    Check ($ui.Output.SelectedItem -eq 'Save DVD folder') 'Folder output remains selectable'
    Check ($ui.SavePath.Text.Contains('Compressr-DVD-')) 'Folder destination visible before preparation'
    $ui.Output.SelectedIndex = 2
    Check ($ui.SaveOptions.Visibility -eq 'Visible' -and $ui.StartButton.Content -eq '_Make DVD' -and
        $ui.SaveLabel.Text.Contains('retryable') -and $ui.DestinationHint.Text.Contains('added to Burn prepared DVDs')) 'Direct burn exposes its retained retryable ISO destination'
    $ui.Output.SelectedIndex = 0
    Check ($ui.SaveOptions.Visibility -eq 'Visible' -and $ui.SavePath.Text.EndsWith('.iso')) 'ISO destination visible before preparation'
    $chosenPath = $ui.SavePath.Text
    $ui.Output.SelectedIndex = 1
    $ui.Output.SelectedIndex = 0
    Check ($ui.SavePath.Text -eq $chosenPath) 'Output switching preserves chosen ISO destination'
    Check ($ui.Inputs.Items.Count -eq 0) 'Empty queue'
    $blank = [Compressr.Core.DvdMedia]::new(7, $true, $true, $true, $true, 2295104, 2295104, 2)
    $recorded = [Compressr.Core.DvdMedia]::new(7, $true, $true, $false, $false, 2295104, 2295104, 1)
    $fixtureDrive = [Compressr.App.DvdDrive]::new('fixture-never-write', 'Fixture writer (no writes)', '')
    [DiscMonitorFixture]::Drives = @($fixtureDrive)
    [DiscMonitorFixture]::Snapshot = [Compressr.Core.DiscInspection]::new($blank, $true, 'fake:1', $null)
    Await-Task (Invoke-Handler 'RefreshReadinessAsync' @($true, $false))
    Check ($ui.Output.SelectedIndex -eq 0) 'Background disc discovery preserves explicit ISO choice'
    Invoke-Handler 'UseAutomaticOutput' @($null, [Windows.RoutedEventArgs]::new())
    Check ($ui.Output.SelectedIndex -eq 2) 'Automatic selects blank DVD without a manual check'
    [DiscMonitorFixture]::Snapshot = [Compressr.Core.DiscInspection]::new($recorded, $true, 'fake:1', $null)
    Await-Task (Invoke-Handler 'RefreshReadinessAsync' @($true, $false))
    Check ($ui.Output.SelectedIndex -eq 2 -and $ui.DiscStatus.Text.Contains('permission')) 'Nonblank supported RW is offered through Make DVD, not erased by refresh'
    [DiscMonitorFixture]::Failure = 'Fixture device inaccessible'
    Await-Task (Invoke-Handler 'RefreshReadinessAsync' @($true, $false))
    Check ($ui.DiscStatus.Text.Contains('Fixture device inaccessible') -and $ui.Options.IsEnabled) 'Read-only detection failure is explicit without blocking editing'
    [DiscMonitorFixture]::Drives = @()
    [DiscMonitorFixture]::Failure = $null
    Await-Task (Invoke-Handler 'RefreshReadinessAsync' @($true, $false))
    $ui.Output.SelectedIndex = 0
    $eraseField = $window.GetType().GetField('erasing', [Reflection.BindingFlags]'Instance,NonPublic')
    $eraseField.SetValue($window, $true)
    Invoke-Handler 'Busy' @($true)
    Check (!$ui.CancelButton.IsEnabled -and !$ui.Options.IsEnabled -and !$ui.BurnOptions.IsEnabled) 'Synchronous IMAPI erasure prevents cancel and concurrent UI operations'
    $eraseField.SetValue($window, $false)
    Invoke-Handler 'Busy' @($false)
    Layout 1008 921
    Screenshot 'interface-empty.png'
    Invoke-Handler 'AddVideo' @('https://youtu.be/first')
    Check ($ui.Inputs.Items.Count -eq 1 -and $ui.QueueSummary.Text.StartsWith('1 video')) 'Single-video summary'
    Invoke-Handler 'AddVideo' @('https://youtu.be/second')
    Check ($ui.Inputs.Items.Count -eq 2 -and $ui.QueueSummary.Text.StartsWith('2 videos')) 'Multiple-video summary'
    $firstVideo = $ui.Inputs.Items[0]
    $secondVideo = $ui.Inputs.Items[1]
    $firstVideo.Rotation = 90
    Check ($firstVideo.IsResolvingTitle -and $ui.Options.IsEnabled -and $ui.CancelButton.IsEnabled -and !$ui.RetryTitleButton.IsEnabled) 'Metadata lookup is cancellable without locking source order controls or permitting duplicate retries'
    Invoke-Handler 'MoveUp' @($null, [Windows.RoutedEventArgs]::new())
    Check ($ui.Inputs.Items[0].Input -eq 'https://youtu.be/second') 'Move up preserves playback order'
    $titleRequests[$secondVideo.Input].SetResult([Compressr.Core.YouTubeInfo]::new('Second video - chapter two with a longer descriptive title to identify this part in the playback queue', 120))
    $titleRequests[$firstVideo.Input].SetResult([Compressr.Core.YouTubeInfo]::new('First video - chapter one with a longer descriptive title to identify this part in the playback queue', 60))
    Await-Task ($firstVideo.MetadataAsync($titleLookup, [Threading.CancellationToken]::None))
    Await-Task ($secondVideo.MetadataAsync($titleLookup, [Threading.CancellationToken]::None))
    Layout 1008 921
    Check ($ui.Inputs.Items[0].Label.StartsWith('Second video - chapter two') -and
        $ui.Inputs.Items[1].Label.StartsWith('First video - chapter one')) 'Resolved titles follow moved rows without sorting by title or completion'
    Check ($firstVideo.Rotation -eq 90 -and $ui.Inputs.SelectedItem -eq $secondVideo) 'Title completion preserves manual rotation and selection'
    $presenter = [Windows.Controls.ContentPresenter]::new()
    $presenter.Content = $secondVideo
    $presenter.ContentTemplate = $ui.Inputs.ItemTemplate
    $presenter.Measure([Windows.Size]::new(500, 60)); $presenter.Arrange([Windows.Rect]::new(0, 0, 500, 60))
    $panel = [Windows.Media.VisualTreeHelper]::GetChild($presenter, 0)
    Check ($panel.Children[0].Text -eq $secondVideo.Label -and $panel.ToolTip -eq $secondVideo.Input) 'Actual queue template binds title and retains original URL tooltip'
    Screenshot 'interface-youtube-titles.png'
    $requestCount = $titleRequests.Count
    Await-Estimate
    Check ($ui.CapacitySummary.Text.StartsWith('About ') -and $ui.Estimate.Text.Contains('3.0 minutes') -and
        $titleRequests.Count -eq $requestCount -and !$ui.CapacityMeter.IsIndeterminate) "Automatic usage reuses resolved metadata without requesting again: $($ui.Estimate.Text)"
    Check ($ui.Options.IsEnabled -and $ui.StartButton.IsEnabled -and $ui.CapacityMeter.Value -gt 0) 'Automatic meter remains usable without locking the queue'
    $singlePercent = $ui.CapacityMeter.Value
    $ui.Capacity.SelectedItem = [Compressr.Core.DiscSize]::DualLayer
    Await-Estimate
    Check ($ui.CapacityMeter.Value -lt $singlePercent -and $titleRequests.Count -eq $requestCount) 'Capacity changes automatically recompute fullness without repeating metadata lookup'
    [DiscMonitorFixture]::Drives = @($fixtureDrive)
    [DiscMonitorFixture]::Snapshot = [Compressr.Core.DiscInspection]::new(
        [Compressr.Core.DvdMedia]::new(9, $true, $true, $true, $true, 1953125, 1953125, 2), $false, $null, $null)
    $ui.Output.SelectedIndex = 2
    Await-Task (Invoke-Handler 'RefreshReadinessAsync' @($true, $false))
    Await-Estimate
    Check ($ui.CapacitySummary.Text.Contains('/ 4.00 GB') -and $ui.Estimate.Text.Contains('actual disc')) "Meter uses actual available media capacity rather than oversized selected target: $($ui.CapacitySummary.Text) / $($ui.Estimate.Text) / $($ui.DiscStatus.Text)"
    [DiscMonitorFixture]::Snapshot = [Compressr.Core.DiscInspection]::new(
        [Compressr.Core.DvdMedia]::new(7, $true, $true, $false, $false, 1953125, 0, 1), $true, 'fake:2', $null)
    Await-Task (Invoke-Handler 'RefreshReadinessAsync' @($true, $false))
    Await-Estimate
    Check ($ui.CapacitySummary.Text.Contains('/ 4.00 GB')) 'Nonblank RW estimate uses post-erase total capacity without erasing'
    Layout 1008 921
    Screenshot 'make-dvd-rewritable-capacity.png'
    [DiscMonitorFixture]::Drives = @()
    Await-Task (Invoke-Handler 'RefreshReadinessAsync' @($true, $false))
    $ui.Output.SelectedIndex = 0
    $ui.Capacity.SelectedItem = [Compressr.Core.DiscSize]::SingleLayer
    Invoke-Handler 'AddVideo' @('https://youtu.be/too-long')
    $titleRequests['https://youtu.be/too-long'].SetResult([Compressr.Core.YouTubeInfo]::new('Too long fixture', 20000))
    Await-Estimate
    Check ($ui.CapacitySummary.Text.StartsWith('Too long') -and $ui.Estimate.Text.Contains('2 Mbps') -and $ui.CapacityMeter.Value -eq 100) 'Too-long state reports quality floor rather than promising auto-fit at unsafe quality'
    Layout 1008 921
    Screenshot 'automatic-capacity-too-long.png'
    Invoke-Handler 'RemoveInput' @($null, [Windows.RoutedEventArgs]::new())
    Await-Estimate
    Check ($ui.CapacitySummary.Text.StartsWith('About ') -and $ui.Estimate.Text.Contains('3.0 minutes')) 'Removal replaces previous capacity result with current source snapshot'
    Invoke-Handler 'AddVideo' @('https://youtu.be/stale')
    $staleDeadline = [DateTime]::UtcNow.AddMilliseconds(450)
    while ([DateTime]::UtcNow -lt $staleDeadline) {
        [Windows.Threading.Dispatcher]::CurrentDispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::Background)
        Start-Sleep -Milliseconds 10
    }
    Check ($ui.CapacityMeter.IsIndeterminate -and $ui.Options.IsEnabled) 'Pending duration displays indeterminate capacity without blocking editing'
    Invoke-Handler 'RemoveInput' @($null, [Windows.RoutedEventArgs]::new())
    $titleRequests['https://youtu.be/stale'].SetResult([Compressr.Core.YouTubeInfo]::new('Removed obsolete result', 30000))
    Await-Estimate
    Check ($ui.CapacitySummary.Text.StartsWith('About ') -and $ui.Estimate.Text.Contains('3.0 minutes')) 'Obsolete in-flight duration result cannot overwrite a newer source snapshot'
    $ui.Inputs.SelectedItem = $secondVideo
    Invoke-Handler 'MoveDown' @($null, [Windows.RoutedEventArgs]::new())
    Check ($ui.Inputs.Items[1].Input -eq 'https://youtu.be/second') 'Move down preserves playback order'
    Invoke-Handler 'ReorderInput' @($firstVideo, 2)
    Check ($ui.Inputs.Items[1] -eq $firstVideo -and $firstVideo.Rotation -eq 90 -and $firstVideo.Metadata.Duration -eq 60) 'Drag insertion keeps source identity, manual rotation and metadata'
    Invoke-Handler 'ReorderInput' @($firstVideo, 0)
    Check ($ui.Inputs.Items[0] -eq $firstVideo) 'Drag insertion supports both directions and first position'
    Layout 1008 921
    $internalDrag = [Windows.DataObject]::new()
    $internalDrag.SetData([Compressr.Core.SourceVideo], $secondVideo)
    $over = Queue-Drag $internalDrag 3 $false
    Check ($over.Handled -and $over.Effects -eq 'Move') 'Native routed internal drag advertises move rather than adding a second source'
    $drop = Queue-Drag $internalDrag 3 $true
    Check ($ui.Inputs.Items[0] -eq $secondVideo -and $ui.Inputs.Items.Count -eq 2) 'Actual preview-drop route moves an existing clip before its target'
    $drop = Queue-Drag $internalDrag ($ui.Inputs.ActualHeight - 3) $true
    Check ($ui.Inputs.Items[1] -eq $secondVideo -and $ui.Inputs.SelectedItem -eq $secondVideo) 'Native routed drop in blank queue space appends while retaining selection'
    Layout 1008 921
    $targetRow = $ui.Inputs.ItemContainerGenerator.ContainerFromIndex(0)
    $center = $targetRow.TranslatePoint([Windows.Point]::new(0, $targetRow.ActualHeight / 2), $ui.Inputs).Y
    $drop = Queue-Drag $internalDrag $center $true
    Check ($ui.Inputs.Items[0] -eq $secondVideo) 'Dropping on the center of the preceding row moves upward rather than doing nothing'
    Layout 1008 921
    $targetRow = $ui.Inputs.ItemContainerGenerator.ContainerFromIndex(1)
    $center = $targetRow.TranslatePoint([Windows.Point]::new(0, $targetRow.ActualHeight / 2), $ui.Inputs).Y
    $drop = Queue-Drag $internalDrag $center $true
    Check ($ui.Inputs.Items[1] -eq $secondVideo) 'Dropping on the center of the next row moves downward rather than doing nothing'
    $data = [Windows.DataObject]::new()
    $data.SetText("https://youtu.be/third`r`nhttps://youtu.be/fourth", [Windows.TextDataFormat]::UnicodeText)
    $ui.Source.Text = 'retained until a valid drop'
    $drop = Drop-Data $data
    Check ($drop.Handled -and $ui.Inputs.Items.Count -eq 4 -and $ui.Source.Text -eq 'retained until a valid drop') 'TextBox preview-drop enqueues links without replacing or losing a pending typed source'
    $ui.Source.Clear()
    foreach ($number in @('third','fourth')) {
        $titleRequests["https://youtu.be/$number"].SetResult([Compressr.Core.YouTubeInfo]::new("Video part $number - a complete descriptive clip title", 60))
    }
    foreach ($item in $ui.Inputs.Items) { Await-Task ($item.MetadataAsync($titleLookup, [Threading.CancellationToken]::None)) }
    Await-Estimate
    Layout 1008 921
    $queueNormalHeight = $ui.Inputs.ActualHeight
    Check ($queueNormalHeight -ge 240) "Normal window queue has room for at least six single-line clips ($queueNormalHeight DIP)"
    Layout 2000 1050
    Check ($ui.Inputs.ActualWidth -gt 1400 -and $ui.Inputs.ActualHeight -gt $queueNormalHeight) 'Maximized workspace expands queue width and height'
    $fourthRow = $ui.Inputs.ItemContainerGenerator.ContainerFromIndex(3)
    Check ($fourthRow.TranslatePoint([Windows.Point]::new(0, $fourthRow.ActualHeight), $ui.Inputs).Y -le $ui.Inputs.ActualHeight) 'All four clips fit without scrolling in the maximized queue'
    Screenshot 'workspace-maximized.png'
    Screenshot 'automatic-capacity-maximized.png'
    $ui.OrientationOptions.IsExpanded = $true
    Layout 1008 921
    Check ($ui.OrientationOptions.Content.ActualHeight -ge 100 -and $ui.Inputs.ActualHeight -ge 180) "Orientation controls remain accessible with useful queue space (content=$($ui.OrientationOptions.Content.ActualHeight), queue=$($ui.Inputs.ActualHeight))"
    Screenshot 'workspace-normal.png'
    Screenshot 'automatic-capacity-normal.png'
    $ui.OrientationOptions.IsExpanded = $false
    $badData = [Windows.DataObject]::new()
    $badData.SetText('https://youtube.com.evil.example/watch?v=no')
    $drop = Drop-Data $badData
    Check ($ui.Inputs.Items.Count -eq 4 -and $ui.Status.Text.Contains('HTTPS YouTube')) 'Unsupported browser URL rejected with visible error and no partial queue changes'
    $urlData = [Windows.DataObject]::new()
    $urlData.SetData('UniformResourceLocatorW', [IO.MemoryStream]::new([Text.Encoding]::Unicode.GetBytes("https://youtu.be/browser`0")))
    $parsed = @(Invoke-Handler 'DroppedSources' @($urlData))
    Check ($parsed.Count -eq 1 -and $parsed[0] -eq 'https://youtu.be/browser') 'Browser URL-link stream format is decoded and validated'
    $badFiles = [Windows.DataObject]::new()
    $missingFiles = [Collections.Specialized.StringCollection]::new()
    $null = $missingFiles.Add((Join-Path $testState 'missing-video.mp4'))
    $badFiles.SetFileDropList($missingFiles)
    $drop = Drop-Data $badFiles
    Check ($ui.Inputs.Items.Count -eq 4 -and $ui.Status.Text.Contains('existing video files')) 'Missing file drop fails explicitly without adding an unusable source'
    $cancelField = $window.GetType().GetField('cancellation', [Reflection.BindingFlags]'Instance,NonPublic')
    $busyToken = [Threading.CancellationTokenSource]::new()
    $cancelField.SetValue($window, $busyToken)
    try {
        $drop = Drop-Data $data
        Invoke-Handler 'ReorderInput' @($firstVideo, 4)
        Check ($drop.Effects -eq 'None' -and $ui.Inputs.Items.Count -eq 4 -and $ui.Inputs.Items[0] -eq $firstVideo) 'Busy operation rejects link drops and reorder without mutation'
    } finally { $cancelField.SetValue($window, $null); $busyToken.Dispose() }
    $ui.Inputs.SelectedIndex = 3
    Invoke-Handler 'RemoveInput' @($null, [Windows.RoutedEventArgs]::new())
    $ui.Inputs.SelectedIndex = 2
    Invoke-Handler 'RemoveInput' @($null, [Windows.RoutedEventArgs]::new())
    Layout 1008 921
    Invoke-Handler 'AddVideo' @('https://youtu.be/failure')
    $failedVideo = $ui.Inputs.SelectedItem
    $titleRequests[$failedVideo.Input].SetException([IO.IOException]::new('Fixture offline'))
    [Windows.Threading.Dispatcher]::CurrentDispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::Background)
    Check ($failedVideo.TitleStatus.Contains('Fixture offline') -and $failedVideo.Label -eq $failedVideo.Input -and $ui.RetryTitleButton.IsEnabled) 'Native row reports lookup failure, keeps the URL and enables retry'
    Await-Estimate
    Check ($ui.CapacitySummary.Text.Contains('unknown') -and $ui.Estimate.Text.Contains('Fixture offline')) 'Incomplete metadata is explicit unknown capacity, not a partial success meter'
    Invoke-Handler 'RetryTitle' @($null, [Windows.RoutedEventArgs]::new())
    $titleRequests[$failedVideo.Input].SetResult([Compressr.Core.YouTubeInfo]::new('Recovered video title', 30))
    Await-Task ($failedVideo.MetadataAsync($titleLookup, [Threading.CancellationToken]::None))
    Check ($failedVideo.Label -eq 'Recovered video title') 'Retry title updates the existing native queue row'
    Invoke-Handler 'RemoveInput' @($null, [Windows.RoutedEventArgs]::new())
    Invoke-Handler 'AddVideo' @('https://youtu.be/cancel')
    $cancelVideo = $ui.Inputs.SelectedItem
    Invoke-Handler 'Cancel' @($null, [Windows.RoutedEventArgs]::new())
    [Windows.Threading.Dispatcher]::CurrentDispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::Background)
    Check ($cancelVideo.TitleStatus.Contains('cancelled') -and $cancelVideo.Metadata -eq $null) 'Cancel stops background title lookup explicitly'
    Invoke-Handler 'RemoveInput' @($null, [Windows.RoutedEventArgs]::new())
    Invoke-Handler 'AddVideo' @('https://youtu.be/remove')
    $removedVideo = $ui.Inputs.SelectedItem
    Invoke-Handler 'RemoveInput' @($null, [Windows.RoutedEventArgs]::new())
    [Windows.Threading.Dispatcher]::CurrentDispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::Background)
    Check (!$removedVideo.IsResolvingTitle -and $removedVideo.Metadata -eq $null) 'Removing a pending row cancels its request without affecting the other rows'
    Invoke-Handler 'Busy' @($true)
    Check (!$ui.Options.IsEnabled -and !$ui.BurnOptions.IsEnabled -and !$ui.StartButton.IsEnabled -and $ui.CancelButton.IsEnabled) 'Preparing locks options, allows cancellation'
    Invoke-Handler 'Busy' @($false)
    Check ($ui.Options.IsEnabled -and $ui.BurnOptions.IsEnabled -and $ui.StartButton.IsEnabled -and !$ui.CancelButton.IsEnabled) 'Idle restores options'
    $ui.WorkspaceTabs.SelectedIndex = 1
    Layout 1008 921
    $drop = Drop-Data $data
    Check ($drop.Effects -eq 'None' -and $ui.Inputs.Items.Count -eq 2) 'Prepared-DVD tab never adds source videos from external drops'
    Check ($ui.Drives.ActualHeight -ge 36 -and $ui.BurnOptions.IsEnabled -and
        $ui.StartButton.Content -eq '_Burn selected DVD…') 'Prepared DVDs are a first-class operation'
    Check ($ui.PreparedJobs.Items.Count -eq 0 -and !$ui.BurnSelectedButton.IsEnabled) 'No implicit prepared job selection'
    if ($PreparedIsoPaths) {
        Await-Task (Invoke-Handler 'AddPreparedPathsAsync' @(,[string[]]$PreparedIsoPaths))
        Check ($ui.PreparedJobs.Items.Count -eq $PreparedIsoPaths.Count) 'Multiple ISOs added as separate jobs'
        Check (@($ui.PreparedJobs.Items | Where-Object Status -Like 'Verified ISO*').Count -eq $PreparedIsoPaths.Count) 'Prepared ISO contents independently validated'
        $ui.PreparedJobs.SelectedIndex = 0
        $firstPath = $ui.PreparedJobs.SelectedItem.Path
        Check ($ui.PreparedPath.Text.Contains($firstPath) -and $ui.BurnSelectedButton.IsEnabled) 'Selecting one ISO updates exact identity without writing'
        if ($PreparedIsoPaths.Count -gt 1) {
            $ui.PreparedJobs.SelectedIndex = 1
            Check ($ui.PreparedJobs.SelectedItem.Path -ne $firstPath) 'Selecting another ISO does not join jobs'
        }
        Await-Task (Invoke-Handler 'AddPreparedPathsAsync' @(,[string[]]@((Join-Path $testState 'missing.iso'))))
        Check ($ui.PreparedJobs.SelectedItem.Status.StartsWith('Invalid / unavailable:')) 'Missing prepared file has actionable status'
        Invoke-Handler 'RemovePrepared' @($null, [Windows.RoutedEventArgs]::new())
        Check ($ui.PreparedJobs.Items.Count -eq $PreparedIsoPaths.Count) 'Remove from list keeps other jobs'
        Check ((Get-Content (Join-Path $testState 'prepared-jobs.json') -Raw | ConvertFrom-Json).Count -eq $PreparedIsoPaths.Count) 'Prepared paths persisted for next run'
    }
    Screenshot 'interface-burn-later.png'
    $ui.WorkspaceTabs.SelectedIndex = 0
    Layout 704 651
    $point = $ui.StartButton.TranslatePoint([Windows.Point]::new(0, 0), $window.Content)
    Check ($point.X -ge 0 -and $point.Y -ge 0 -and
        $point.X + $ui.StartButton.ActualWidth -le 704 -and
        $point.Y + $ui.StartButton.ActualHeight -le 651) 'Primary action fits compact 720 x 690 window'
    Check ($ui.Source.ActualWidth -ge 200) 'Compact source field remains usable'
    Check ($ui.Inputs.ActualHeight -ge 140) 'Compact window retains dedicated queue viewport'
    $queueBottom = $ui.Inputs.TranslatePoint([Windows.Point]::new(0, $ui.Inputs.ActualHeight), $window.Content).Y
    $buttonsTop = $ui.RetryTitleButton.TranslatePoint([Windows.Point]::new(0, 0), $window.Content).Y
    Check ($queueBottom -le $buttonsTop) 'Compact queue is fully visible without overlapping its action controls'
    Screenshot 'interface-compact.png'
    Layout 804 681
    $point = $ui.StartButton.TranslatePoint([Windows.Point]::new(0, 0), $window.Content)
    Check ($point.X + $ui.StartButton.ActualWidth -le 804 -and
        $point.Y + $ui.StartButton.ActualHeight -le 681) 'Action remains in view at a 125-percent-sized viewport'
    Invoke-Handler 'RemoveInput' @($null, [Windows.RoutedEventArgs]::new())
    Check ($ui.Inputs.Items.Count -eq 1) 'Remove video'
    Invoke-Handler 'RemoveInput' @($null, [Windows.RoutedEventArgs]::new())
    Check ($ui.Inputs.Items.Count -eq 0 -and $ui.QueueSummary.Text -eq 'Play order') 'Return to empty state'
    if ($SourceVideoPaths) {
        $fileDrop = [Windows.DataObject]::new()
        $files = [Collections.Specialized.StringCollection]::new()
        foreach ($video in $SourceVideoPaths) { $null = $files.Add($video) }
        $fileDrop.SetFileDropList($files)
        $drop = Drop-Data $fileDrop
        Check ($drop.Handled -and $ui.Inputs.Items.Count -eq $SourceVideoPaths.Count -and $ui.Inputs.Items[0].IsLocal) 'Native file drop still adds existing local videos without remote title lookups'
        Await-Estimate
        Check ($ui.CapacitySummary.Text.StartsWith('About ') -and $ui.CapacityMeter.Value -gt 0) 'Local generated clips populate live capacity meter without a manual check or video conversion'
        while ($ui.Inputs.Items.Count -gt 0) { Invoke-Handler 'RemoveInput' @($null, [Windows.RoutedEventArgs]::new()) }
        $destination = Join-Path $testState 'source-job.iso'
        Invoke-Handler 'SetIsoDestination' @([string]$destination)
        $ui.Output.SelectedIndex = 0
        Invoke-Handler 'UpdateOutput' @()
        foreach ($video in $SourceVideoPaths) { Invoke-Handler 'AddVideo' @([string]$video) }
        Invoke-Handler 'Start' @($null, [Windows.RoutedEventArgs]::new())
        Await-Idle
        Check ((Test-Path -LiteralPath $destination) -and $ui.Stage.Text -eq 'Done') "Source-to-ISO workflow: $($ui.Status.Text)"
        Check ($ui.PreparedJobs.SelectedItem.Path -eq $destination) 'Prepared source job added to retained ISO list'
        Check ($ui.Output.SelectedIndex -eq 0) 'Preparation never overrides manual file choice'
        $ui.WorkspaceTabs.SelectedIndex = 1
        Layout 1008 921
        Screenshot 'interface-prepared-job.png'
        $restored = $constructor.Invoke([object[]]@([string]$testState, $titleLookup, [DiscMonitorFixture]::Enumerate, [DiscMonitorFixture]::Inspect))
        try {
            $restoredJobs = $restored.FindName('PreparedJobs')
            Check ($restoredJobs.Items.Count -eq $ui.PreparedJobs.Items.Count) 'Prepared list reloads across app windows'
            Check (@($restoredJobs.Items | Where-Object Path -EQ $destination).Count -eq 1) 'Self-contained saved image identity restored'
            Check ($restored.FindName('SavePath').Text -eq $destination) 'Custom ISO destination persists across app windows'
            } finally { $restored.Close() }
            $ui.WorkspaceTabs.SelectedIndex = 0
            $ui.Output.SelectedIndex = 1
            $folder = Join-Path $testState 'dvd-folder'
            Invoke-Handler 'SetFolderDestination' @([string]$folder)
        Invoke-Handler 'Start' @($null, [Windows.RoutedEventArgs]::new())
        Await-Idle
        Check ((Test-Path (Join-Path $folder 'VIDEO_TS\VIDEO_TS.IFO')) -and $ui.Stage.Text -eq 'Done') 'Source-to-DVD-folder workflow retained'
        $ui.Output.SelectedIndex = 2
        Invoke-Handler 'SetIsoDestination' @([string](Join-Path $testState 'no-writer.iso'))
        Invoke-Handler 'Start' @($null, [Windows.RoutedEventArgs]::new())
        Await-Idle
        Check ($ui.Stage.Text -eq 'Could not complete DVD' -and $ui.Status.Text.Contains('No DVD writer selected')) 'Direct burn without a writer fails explicitly before preparation'
        Check ($ui.Output.SelectedIndex -eq 2) 'Failed direct burn never silently falls back to file'
        $driveType = $window.GetType().Assembly.GetType('Compressr.App.DvdDrive')
        $unavailable = [Activator]::CreateInstance($driveType, [object[]]@('unavailable-test-recorder', 'Unavailable test recorder', ''))
        [DiscMonitorFixture]::Drives = @($unavailable)
        [DiscMonitorFixture]::Failure = 'Fixture device inaccessible'
        Await-Task (Invoke-Handler 'RefreshReadinessAsync' @($true, $false))
        Invoke-Handler 'Start' @($null, [Windows.RoutedEventArgs]::new())
        Await-Idle
        Check ($ui.Stage.Text -eq 'Could not complete DVD' -and $ui.Status.Text -notmatch 'calling thread|different thread owns it') 'Unavailable recorder reaches IMAPI preflight without accessing UI controls from its STA thread'
        Check ($ui.Output.SelectedIndex -eq 2 -and @(Get-ChildItem (Join-Path $testState 'jobs') -Filter '*.iso' -Recurse -ErrorAction SilentlyContinue).Count -eq 0) 'Unavailable recorder never starts conversion or falls back to an ISO'
        Invoke-Handler 'UseAutomaticOutput' @($null, [Windows.RoutedEventArgs]::new())
        Invoke-Handler 'Start' @($null, [Windows.RoutedEventArgs]::new())
        Await-Idle
        Check ($ui.Stage.Text -eq 'Could not complete DVD' -and $ui.Status.Text.Contains('Nothing was started')) 'Failed fresh automatic detection never silently becomes a file-writing action'

        [DiscMonitorFixture]::Failure = $null
        $retryDestination = Join-Path $testState 'recovery\retryable.iso'
        [IO.Directory]::CreateDirectory((Split-Path -Parent $retryDestination)) | Out-Null
        $ui.Output.SelectedIndex = 2
        [DiscMonitorFixture]::Drives = @($fixtureDrive)
        [DiscMonitorFixture]::Snapshot = [Compressr.Core.DiscInspection]::new($blank, $true, 'fake:retry')
        Await-Task (Invoke-Handler 'RefreshReadinessAsync' @($true, $false))
        Await-Estimate
        Invoke-Handler 'SetIsoDestination' @([string]$retryDestination)
        Check ($ui.SavePath.Text -eq $retryDestination -and
            (Get-Content (Join-Path $testState 'dvd-destinations.json') -Raw | ConvertFrom-Json).Iso -eq $retryDestination) 'Direct-burn retry destination is visible and durably persisted'
        $ui.Stage.Text = 'Ready'
        $ui.Status.Text = 'The verified DVD-ready ISO will be kept at this location before any disc is written.'
        Layout 1008 921
        Screenshot 'retryable-iso-destination.png'

        [DiscMonitorFixture]::StateDirectory = $testState
        [DiscMonitorFixture]::Snapshot = [Compressr.Core.DiscInspection]::new($blank, $true, 'fake:retry')
        [DiscMonitorFixture]::Drives = @($fixtureDrive)
        [DiscMonitorFixture]::WriteBehavior = 'fail'
        [DiscMonitorFixture]::VerifyBehavior = 'pass'
        $writeType = [Func[Compressr.Core.PreparedDvd, Compressr.App.DvdDrive, IProgress[Compressr.Core.StageProgress], Threading.CancellationToken, Threading.Tasks.Task]]
        $verifyType = $writeType
        $confirmType = [Func[Compressr.Core.PreparedDvd, Compressr.App.DvdDrive, bool]]
        $writeDelegate = [Delegate]::CreateDelegate($writeType, [DiscMonitorFixture].GetMethod('Write'))
        $verifyDelegate = [Delegate]::CreateDelegate($verifyType, [DiscMonitorFixture].GetMethod('Verify'))
        $confirmDelegate = [Delegate]::CreateDelegate($confirmType, [DiscMonitorFixture].GetMethod('Confirm'))
        $injectedTypes = [Type[]]@([string], $titleLookup.GetType(), [DiscMonitorFixture]::Enumerate.GetType(),
            [DiscMonitorFixture]::Inspect.GetType(), $writeType, $verifyType, $confirmType)
        $injectedCtor = [Compressr.App.MainWindow].GetConstructor(
            [Reflection.BindingFlags]'Instance,NonPublic', $null, $injectedTypes, $null)
        $retryWindow = $injectedCtor.Invoke([object[]]@([string]$testState, $titleLookup,
            [DiscMonitorFixture]::Enumerate, [DiscMonitorFixture]::Inspect, $writeDelegate, $verifyDelegate, $confirmDelegate))
        $previousWindow = $window
        $previousUi = $ui
        try {
            $window = $retryWindow
            $ui = @{
                Output = $retryWindow.FindName('Output')
                Stage = $retryWindow.FindName('Stage')
                Status = $retryWindow.FindName('Status')
                SavePath = $retryWindow.FindName('SavePath')
            }
            $retryOutput = $retryWindow.FindName('Output')
            $retryStateField = [Compressr.App.MainWindow].GetField('isoDestination', [Reflection.BindingFlags]'Instance,NonPublic')
            Check ($retryStateField.GetValue($retryWindow) -eq $retryDestination) 'New session restores selected direct-burn ISO destination'
            foreach ($video in $SourceVideoPaths) { $retryWindow.GetType().GetMethod('AddVideo', [Reflection.BindingFlags]'Instance,NonPublic').Invoke($retryWindow, [object[]]@([string]$video)) }
            $retryWindow.GetType().GetMethod('SetIsoDestination', [Reflection.BindingFlags]'Instance,NonPublic').Invoke($retryWindow, [object[]]@([string]$retryDestination))
            $retryOutput.SelectedIndex = 2
            [DiscMonitorFixture]::Writes = 0
            Invoke-Handler 'Start' @($null, [Windows.RoutedEventArgs]::new())
            Await-Idle
            Write-Host "Retry fixture after direct-burn preparation: stage=$($ui.Stage.Text); writes=$([DiscMonitorFixture]::Writes); registered=$([DiscMonitorFixture]::RegisteredBeforeWrite); status=$($ui.Status.Text)"
            Check ([DiscMonitorFixture]::Writes -eq 1 -and [DiscMonitorFixture]::RegisteredBeforeWrite) 'Complete ISO and retry-list entry are durable before injected burn begins'
            Check ((Test-Path -LiteralPath $retryDestination) -and $ui.Output.SelectedIndex -eq 2) 'Simulated write failure keeps the prepared ISO and explicit burn choice'
            Check ($ui.Stage.Text -eq 'Disc not verified' -and $ui.Status.Text.Contains('Select it there to retry')) 'Failed direct burn gives exact retry path without rerunning conversion'
            Check ((Get-Content (Join-Path $testState 'prepared-jobs.json') -Raw | ConvertFrom-Json) -contains $retryDestination) 'Failed direct burn job is persisted across restart'
            Layout 1008 921
            Screenshot 'retryable-iso-after-failed-burn.png'

            $previousTools = $env:COMPRESSR_TOOLS
            $env:COMPRESSR_TOOLS = Join-Path $testState 'intentionally-missing-tools'
            $reopened = $injectedCtor.Invoke([object[]]@([string]$testState, $titleLookup,
                [DiscMonitorFixture]::Enumerate, [DiscMonitorFixture]::Inspect, $writeDelegate, $verifyDelegate, $confirmDelegate))
            $priorWindow = $window
            $priorUi = $ui
            try {
                $window = $reopened
                $ui = @{
                    Output = $reopened.FindName('Output')
                    Stage = $reopened.FindName('Stage')
                    Status = $reopened.FindName('Status')
                }
                $reopenedJobs = $reopened.FindName('PreparedJobs')
                $reopenedInputs = $reopened.FindName('Inputs')
                $reopenedTabs = $reopened.FindName('WorkspaceTabs')
                $reopenedJobs.SelectedItem = @($reopenedJobs.Items | Where-Object Path -EQ $retryDestination)[0]
                Check ($reopenedJobs.Items.Count -ge 1 -and $reopenedInputs.Items.Count -eq 0) 'Restart restores prepared image without original source list'
                $reopenedTabs.SelectedIndex = 1
                [DiscMonitorFixture]::WriteBehavior = 'cancel'
                [DiscMonitorFixture]::VerifyBehavior = 'pass'
                Invoke-Handler 'BurnPrepared' @($null, [Windows.RoutedEventArgs]::new())
                Await-Idle
                Check ($window.FindName('Stage').Text -eq 'Cancelled' -and (Test-Path -LiteralPath $retryDestination)) 'Simulated burn cancellation preserves reusable prepared ISO'
                [DiscMonitorFixture]::WriteBehavior = 'pass'
                [DiscMonitorFixture]::VerifyBehavior = 'crc'
                Invoke-Handler 'BurnPrepared' @($null, [Windows.RoutedEventArgs]::new())
                Await-Idle
                Check ($window.FindName('Stage').Text -eq 'Disc not verified' -and $window.FindName('Status').Text.Contains('Simulated read-back CRC') -and
                    (Test-Path -LiteralPath $retryDestination)) 'Simulated read-back failure preserves ISO and reports it unverified'
            } finally { $window = $priorWindow; $ui = $priorUi; $reopened.Close(); $env:COMPRESSR_TOOLS = $previousTools }
        } finally { $window = $previousWindow; $ui = $previousUi; $retryWindow.Close() }
        [DiscMonitorFixture]::Drives = @()
        [DiscMonitorFixture]::Failure = $null
    }
    foreach ($pair in @(
        @('#183344', '#FFFFFF'), @('#506575', '#FFFFFF'), @('#506575', '#EDF2F5'),
        @('#FFFFFF', '#006D77'), @('#FFFFFF', '#005761'), @('#FFFFFF', '#004650'),
        @('#CBDFE8', '#183344'), @('#183344', '#D7ECEE'))) {
        $a = Luminance $pair[0]; $b = Luminance $pair[1]
        $ratio = ([Math]::Max($a, $b) + .05) / ([Math]::Min($a, $b) + .05)
        Check ($ratio -ge 4.5) "Text contrast $($pair[0]) / $($pair[1])"
        Write-Host ("Contrast {0} / {1}: {2:F2}:1" -f $pair[0], $pair[1], $ratio)
    }
    $ui.WorkspaceTabs.SelectedIndex = 0
    [DiscMonitorFixture]::Drives = @()
    [DiscMonitorFixture]::Failure = $null
    Await-Task (Invoke-Handler 'RefreshReadinessAsync' @($true, $false))
    $ui.Output.SelectedIndex = 0
    $window.GetType().GetField('isoDestination', [Reflection.BindingFlags]'Instance,NonPublic').SetValue(
        $window, [string](Join-Path $testState 'Compressr.iso'))
    $window.GetType().GetField('automaticIsoName', [Reflection.BindingFlags]'Instance,NonPublic').SetValue($window, $true)
    Invoke-Handler 'UpdateOutput' @()
    while ($ui.Inputs.Items.Count -gt 0) { Invoke-Handler 'RemoveInput' @($null, [Windows.RoutedEventArgs]::new()) }
    foreach ($number in @(1, 2, 3)) {
        $url = "https://youtu.be/progress-fixture-$number"
        Invoke-Handler 'AddVideo' @($url)
        $titleRequests[$url].SetResult([Compressr.Core.YouTubeInfo]::new("Generated sample video $number", 120))
    }
    Await-Estimate
    $ui.Inputs.SelectedIndex = 1
    Layout 1008 921
    Check ($ui.SavePath.Text.EndsWith('Generated sample video 1 and 2 more.iso')) 'Visible automatic destination uses source title and clip count'
    $ui.Stage.Text = 'Ready'
    $ui.Status.Text = 'Generated metadata fixtures only. No user video is downloaded and no disc is written.'
    Layout 1008 921
    Screenshot 'source-based-iso-name.png'
    Invoke-Handler 'Busy' @($true)
    $reporter = Invoke-Handler 'Reporter' @()
    $value = [Compressr.Core.DownloadProgress]::Parse(
        'COMPRESSR_DOWNLOAD:{"status":"downloading","downloaded_bytes":25000000,"total_bytes":100000000,"speed":2000000,"eta":38}',
        'Video 2 of 3 · Generated sample video 2')
    $reporter.Report($value)
    [Windows.Threading.Dispatcher]::CurrentDispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::Background)
    Check ($ui.Stage.Text -eq 'Download' -and $ui.Progress.Value -eq 25 -and !$ui.Progress.IsIndeterminate -and
        $ui.Status.Text.Contains('Video 2 of 3') -and $ui.Status.Text.Contains('ETA 0:38')) 'Native UI displays real parsed bytes/percent/speed/ETA and clip identity'
    Screenshot 'download-progress.png'
    $reporter.Report([Compressr.Core.DownloadProgress]::Parse(
        'COMPRESSR_DOWNLOAD:{"status":"downloading","downloaded_bytes":1000000,"total_bytes":null,"eta":null}', 'Video 2 of 3'))
    [Windows.Threading.Dispatcher]::CurrentDispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::Background)
    Check ($ui.Progress.IsIndeterminate -and $ui.Status.Text.Contains('ETA unavailable')) 'Unknown download time is explicit in native UI'
    $reporter.Report([Compressr.Core.DownloadProgress]::Parse('[Merger] Merging video and audio', 'Video 2 of 3 · Generated sample video 2'))
    [Windows.Threading.Dispatcher]::CurrentDispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::Background)
    Check ($ui.Stage.Text -eq 'Merge' -and $ui.Progress.IsIndeterminate -and $ui.Status.Text.Contains('merging has no percentage')) 'Native merge UI never invents a percentage'
    Screenshot 'download-merge.png'
    Invoke-Handler 'Busy' @($false)
    Await-Estimate
    $ui.Stage.Text = 'Disc not verified'
    $ui.Status.Text = [Compressr.Core.DvdReadbackException]::new('D:\VIDEO_TS\VTS_01_1.VOB', 1048576,
        'Windows reported a cyclic redundancy check (CRC) read error (error 23). Check the disc, drive and connection.', $true, $null).Message
    Layout 1008 921
    Check ($ui.Status.ActualHeight -le 76 -and $ui.Status.Text.Contains('NOT verified')) 'Detailed CRC recovery stays readable and scrollable without hiding the main action'
    Screenshot 'dvd-read-error.png'
    [DiscMonitorFixture]::Drives = @($fixtureDrive)
    [DiscMonitorFixture]::Snapshot = [Compressr.Core.DiscInspection]::new($blank, $false, $null)
    Await-Task (Invoke-Handler 'RefreshReadinessAsync' @($true, $false))
    $ui.Output.SelectedIndex = 2
    Await-Estimate
    $ui.Stage.Text = 'Disc ready'
    $ui.Status.Text = 'Blank writable DVD: no erase or separate formatting is needed. This is a no-write fixture preview.'
    Layout 1008 921
    Screenshot 'blank-disc-no-erase.png'
    $window.GetType().GetField('burning', [Reflection.BindingFlags]'Instance,NonPublic').SetValue($window, $true)
    Invoke-Handler 'Busy' @($true)
    $burnSnapshot = [Compressr.Core.BurnProgressSnapshot]::new(4, 100, 1000, 599, 60, 90, 150)
    $burnUpdate = [Compressr.Core.BurnProgress]::Describe($burnSnapshot)
    $reporter.Report($burnUpdate)
    [Windows.Threading.Dispatcher]::CurrentDispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::Background)
    Check ($ui.Progress.Value -eq 50 -and !$ui.Progress.IsIndeterminate -and $ui.Status.Text.Contains('1:30 remaining') -and !$ui.CancelButton.IsEnabled) 'Main burn UI renders sector percentage and real writer ETA fields without offering unsafe cancellation'
    $window.Title = 'Compressr - progress fixture (no disc write)'
    Screenshot 'burn-progress.png'
    if ($ui.PreparedJobs.Items.Count -gt 0) {
        $ui.WorkspaceTabs.SelectedIndex = 1
        $ui.PreparedJobs.SelectedIndex = 0
        Layout 1008 921
        Screenshot 'burn-progress-prepared.png'
        $ui.WorkspaceTabs.SelectedIndex = 0
    }
    $reporter.Report([Compressr.Core.BurnProgress]::Describe([Compressr.Core.BurnProgressSnapshot]::new(5, 100, 1000, 1099, 150, 12, 162)))
    [Windows.Threading.Dispatcher]::CurrentDispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::Background)
    Check ($ui.Stage.Text.Contains('finalising') -and $ui.Progress.IsIndeterminate -and !$ui.Status.Text.Contains('verified by SHA')) 'Finalisation is a separate stage, not verified success'
    Screenshot 'burn-finalising.png'
    $reporter.Report([Compressr.Core.BurnProgress]::Describe([Compressr.Core.BurnProgressSnapshot]::new(6, 100, 1000, 1099, 150, 0, 150)))
    [Windows.Threading.Dispatcher]::CurrentDispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::Background)
    Check ($ui.Status.Text.Contains('not yet verified') -and $ui.Progress.IsIndeterminate) 'Write-complete event still requires strict app read-back'
    $window.GetType().GetField('burning', [Reflection.BindingFlags]'Instance,NonPublic').SetValue($window, $false)
    Invoke-Handler 'Busy' @($false)
    Write-Host "PASS: $script:checks native interface assertions (no media downloaded or disc written)"
} finally {
    $window.Close()
    $app.Shutdown()
    if (Test-Path -LiteralPath $testState) { Remove-Item -LiteralPath $testState -Recurse -Force }
    $env:COMPRESSR_TOOLS = $previousTools
}
