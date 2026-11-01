param([string]$ScreenshotDirectory)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Core' -or [Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    throw 'Run with PowerShell 7 on Windows in STA mode: pwsh -STA -File scripts\Test-Interface.ps1'
}
$root = Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName PresentationFramework
Add-Type -Path (Join-Path $root 'Compressr.Core\bin\Release\net8.0\Compressr.Core.dll')
Add-Type -Path (Join-Path $root 'Compressr.App\bin\Release\net8.0-windows\Compressr.dll')
$app = [Compressr.App.App]::new()
$app.InitializeComponent()
$window = [Compressr.App.MainWindow]::new()
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
        'Output', 'Drives', 'Options', 'BurnOptions', 'StartButton', 'CancelButton', 'Stage', 'Progress', 'Status')) {
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
    Check ($ui.Capacity.SelectedItem -eq [Compressr.Core.DiscSize]::DualLayer) 'Dual-layer option remains selectable'
    $ui.Capacity.SelectedItem = [Compressr.Core.DiscSize]::SingleLayer
    $ui.Output.SelectedIndex = 1
    Check ($ui.Output.SelectedItem -eq 'DVD folder') 'Folder output remains selectable'
    $ui.Output.SelectedIndex = 0
    Check ($ui.Inputs.Items.Count -eq 0) 'Empty queue'
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
    $expander = $ui.BurnOptions.Parent
    $expander.IsExpanded = $true
    Layout 1008 921
    Check ($expander.IsExpanded -and $ui.Drives.ActualHeight -ge 36 -and $ui.BurnOptions.IsEnabled) 'Burn-later controls remain accessible'
    Screenshot 'interface-burn-later.png'
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
}
