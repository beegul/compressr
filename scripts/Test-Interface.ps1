param([string]$ScreenshotDirectory, [string[]]$PreparedIsoPaths, [string[]]$SourceVideoPaths)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Core' -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    throw 'Run with PowerShell 7 on Windows in STA mode: pwsh -STA -File scripts\Test-Interface.ps1'
}
$root = Split-Path $PSScriptRoot -Parent
$previousTools = $env:COMPRESSR_TOOLS
if ($SourceVideoPaths -and !$env:COMPRESSR_TOOLS) { $env:COMPRESSR_TOOLS = Join-Path $root 'tools' }
Add-Type -AssemblyName PresentationFramework
Add-Type -Path (Join-Path $root 'Compressr.Core\bin\Release\net8.0\Compressr.Core.dll')
Add-Type -Path (Join-Path $root 'Compressr.App\bin\Release\net8.0-windows\Compressr.dll')
$app = [Compressr.App.App]::new()
$app.InitializeComponent()
[Threading.SynchronizationContext]::SetSynchronizationContext(
    [Windows.Threading.DispatcherSynchronizationContext]::new([Windows.Threading.Dispatcher]::CurrentDispatcher))
$testState = Join-Path $root ("artifacts\interface-state-" + [Guid]::NewGuid().ToString('N'))
$constructor = [Compressr.App.MainWindow].GetConstructor([Reflection.BindingFlags]'Instance,NonPublic',
    $null, [Type[]]@([string]), $null)
$window = $constructor.Invoke([object[]]@([string]$testState))
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
    foreach ($name in @('Source', 'Inputs', 'Estimate', 'Preview', 'RotationLabel', 'Standard', 'Capacity',
        'Output', 'Drives', 'Options', 'BurnOptions', 'StartButton', 'CancelButton', 'Stage', 'Progress', 'Status',
        'WorkspaceTabs', 'SavePath', 'SaveOptions', 'OutputChoiceHint', 'PreparedJobs', 'PreparedPath', 'DiscStatus',
        'BurnSelectedButton', 'RemovePreparedButton', 'PrepareDiscButton', 'SourcePrepareDiscButton')) {
        Check ($null -ne $window.FindName($name)) "Retained $name"
        $ui[$name] = $window.FindName($name)
    }
    $ui.QueueSummary = $window.FindName('QueueSummary')
    [xml]$xaml = Get-Content (Join-Path $root 'Compressr.App\MainWindow.xaml') -Raw
    foreach ($attribute in $xaml.SelectNodes('//@*')) {
        if ($attribute.LocalName -in @('Click', 'Drop', 'DragOver', 'Closing', 'SelectionChanged', 'TextChanged')) {
            Check ($null -ne $window.GetType().GetMethod($attribute.Value, [Reflection.BindingFlags]'Instance,NonPublic,DeclaredOnly')) "Handler $($attribute.Value)"
        }
    }
    Check ($window.FontSize -eq 14) 'Readable 14 DIP body typography'
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
    Check ($ui.SaveOptions.Visibility -eq 'Collapsed' -and $ui.StartButton.Content -eq '_Prepare and burn…') 'Direct burn has a distinct primary action'
    $ui.Output.SelectedIndex = 0
    Check ($ui.SaveOptions.Visibility -eq 'Visible' -and $ui.SavePath.Text.EndsWith('.iso')) 'ISO destination visible before preparation'
    $chosenPath = $ui.SavePath.Text
    $ui.Output.SelectedIndex = 1
    $ui.Output.SelectedIndex = 0
    Check ($ui.SavePath.Text -eq $chosenPath) 'Output switching preserves chosen ISO destination'
    Check ($ui.Inputs.Items.Count -eq 0) 'Empty queue'
    $blank = [Compressr.Core.DvdMedia]::new(7, $true, $true, $true, $true, 2295104, 2295104, 2)
    $recorded = [Compressr.Core.DvdMedia]::new(7, $true, $true, $false, $false, 2295104, 2295104, 1)
    Invoke-Handler 'SetPreparationAvailable' @([Compressr.Core.DiscInspection]::new($blank, $true, 'fake:1', $null))
    Check (!$ui.PrepareDiscButton.IsEnabled -and !$ui.SourcePrepareDiscButton.IsEnabled) 'Blank DVD needs no erase action'
    Invoke-Handler 'SetPreparationAvailable' @([Compressr.Core.DiscInspection]::new($recorded, $true, 'fake:1', $null))
    Check ($ui.PrepareDiscButton.IsEnabled -and $ui.SourcePrepareDiscButton.IsEnabled) 'Supported nonblank RW exposes explicit preparation in both workflows'
    Check ($ui.Output.SelectedIndex -eq 0) 'Making erasure available never changes output or starts a burn'
    Invoke-Handler 'SetPreparationAvailable' @([Compressr.Core.DiscInspection]::new($recorded, $false, $null, 'unsupported'))
    Check (!$ui.PrepareDiscButton.IsEnabled -and !$ui.SourcePrepareDiscButton.IsEnabled) 'Unsupported erasure stays disabled'
    $eraseField = $window.GetType().GetField('erasing', [Reflection.BindingFlags]'Instance,NonPublic')
    $eraseField.SetValue($window, $true)
    Invoke-Handler 'Busy' @($true)
    Check (!$ui.CancelButton.IsEnabled -and !$ui.Options.IsEnabled -and !$ui.BurnOptions.IsEnabled) 'Synchronous IMAPI erasure prevents cancel and concurrent UI operations'
    $eraseField.SetValue($window, $false)
    Invoke-Handler 'Busy' @($false)
    Invoke-Handler 'PrepareDisc' @($null, [Windows.RoutedEventArgs]::new())
    Await-Idle
    Check ($ui.Stage.Text -eq 'Disc not ready' -and $ui.Status.Text.Contains('No DVD writer selected')) 'Preparation without a writer fails explicitly without confirmation'
    Layout 1008 921
    Screenshot 'interface-empty.png'
    Invoke-Handler 'AddVideo' @('https://youtu.be/first')
    Check ($ui.Inputs.Items.Count -eq 1 -and $ui.QueueSummary.Text.StartsWith('1 video')) 'Single-video summary'
    Invoke-Handler 'AddVideo' @('https://youtu.be/second')
    Check ($ui.Inputs.Items.Count -eq 2 -and $ui.QueueSummary.Text.StartsWith('2 videos')) 'Multiple-video summary'
    Invoke-Handler 'MoveUp' @($null, [Windows.RoutedEventArgs]::new())
    Check ($ui.Inputs.Items[0].ToString() -eq 'https://youtu.be/second') 'Move up preserves playback order'
    Invoke-Handler 'MoveDown' @($null, [Windows.RoutedEventArgs]::new())
    Check ($ui.Inputs.Items[1].ToString() -eq 'https://youtu.be/second') 'Move down preserves playback order'
    Invoke-Handler 'Busy' @($true)
    Check (!$ui.Options.IsEnabled -and !$ui.BurnOptions.IsEnabled -and !$ui.StartButton.IsEnabled -and $ui.CancelButton.IsEnabled) 'Preparing locks options, allows cancellation'
    Invoke-Handler 'Busy' @($false)
    Check ($ui.Options.IsEnabled -and $ui.BurnOptions.IsEnabled -and $ui.StartButton.IsEnabled -and !$ui.CancelButton.IsEnabled) 'Idle restores options'
    $ui.WorkspaceTabs.SelectedIndex = 1
    Layout 1008 921
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
        $destination = Join-Path $testState 'source-job.iso'
        $window.GetType().GetField('isoDestination', [Reflection.BindingFlags]'Instance,NonPublic').SetValue($window, [string]$destination)
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
        $restored = $constructor.Invoke([object[]]@([string]$testState))
        try {
            $restoredJobs = $restored.FindName('PreparedJobs')
            Check ($restoredJobs.Items.Count -eq $ui.PreparedJobs.Items.Count) 'Prepared list reloads across app windows'
            Check (@($restoredJobs.Items | Where-Object Path -EQ $destination).Count -eq 1) 'Self-contained saved image identity restored'
        } finally { $restored.Close() }
        $ui.WorkspaceTabs.SelectedIndex = 0
        $ui.Output.SelectedIndex = 1
        $folder = Join-Path $testState 'dvd-folder'
        $window.GetType().GetField('folderDestination', [Reflection.BindingFlags]'Instance,NonPublic').SetValue($window, [string]$folder)
        Invoke-Handler 'UpdateOutput' @()
        Invoke-Handler 'Start' @($null, [Windows.RoutedEventArgs]::new())
        Await-Idle
        Check ((Test-Path (Join-Path $folder 'VIDEO_TS\VIDEO_TS.IFO')) -and $ui.Stage.Text -eq 'Done') 'Source-to-DVD-folder workflow retained'
        $ui.Output.SelectedIndex = 2
        Invoke-Handler 'Start' @($null, [Windows.RoutedEventArgs]::new())
        Await-Idle
        Check ($ui.Stage.Text -eq 'Could not complete DVD' -and $ui.Status.Text.Contains('No DVD writer selected')) 'Direct burn without a writer fails explicitly before preparation'
        Check ($ui.Output.SelectedIndex -eq 2) 'Failed direct burn never silently falls back to file'
        $driveType = $window.GetType().Assembly.GetType('Compressr.App.DvdDrive')
        $unavailable = [Activator]::CreateInstance($driveType, [object[]]@('unavailable-test-recorder', 'Unavailable test recorder', ''))
        $ui.Drives.ItemsSource = @($unavailable)
        $ui.Drives.SelectedIndex = 0
        Await-Idle
        Invoke-Handler 'Start' @($null, [Windows.RoutedEventArgs]::new())
        Await-Idle
        Check ($ui.Stage.Text -eq 'Could not complete DVD' -and $ui.Status.Text -notmatch 'calling thread|different thread owns it') 'Unavailable recorder reaches IMAPI preflight without accessing UI controls from its STA thread'
        Check ($ui.Output.SelectedIndex -eq 2 -and @(Get-ChildItem (Join-Path $testState 'jobs') -Filter '*.iso' -Recurse -ErrorAction SilentlyContinue).Count -eq 0) 'Unavailable recorder never starts conversion or falls back to an ISO'
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
    Write-Host "PASS: $script:checks native interface assertions (no media downloaded or disc written)"
} finally {
    $window.Close()
    $app.Shutdown()
    if (Test-Path -LiteralPath $testState) { Remove-Item -LiteralPath $testState -Recurse }
    $env:COMPRESSR_TOOLS = $previousTools
}
