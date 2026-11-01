param([Parameter(Mandatory)][string]$BundleDirectory)
$ErrorActionPreference = 'Stop'
function Invoke-Placement {
$root = Split-Path $PSScriptRoot -Parent
$BundleDirectory = (Resolve-Path -LiteralPath $BundleDirectory).Path.TrimEnd('\')
if ($BundleDirectory -eq $root) { throw 'Choose a separate complete portable bundle as the source.' }
foreach ($relative in @('Compressr.exe', 'Compressr.dll', 'coreclr.dll', 'hostfxr.dll',
    'Compressr.runtimeconfig.json', 'tools\ffmpeg.exe', 'tools\ffprobe.exe',
    'tools\yt-dlp.exe', 'tools\deno.exe',
    'tools\DVDStylerPortable\App\DVDStyler\bin\dvdauthor.exe',
    'tools\DVDStylerPortable\App\DVDStyler\bin\mkisofs.exe')) {
    if (!(Test-Path -LiteralPath (Join-Path $BundleDirectory $relative) -PathType Leaf)) {
        throw "Incomplete portable bundle: missing $relative"
    }
}
$manifestPath = Join-Path $BundleDirectory 'bundle-manifest.json'
if (!(Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Bundle manifest is required.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
foreach ($entry in $manifest) {
    $file = [IO.Path]::GetFullPath((Join-Path $BundleDirectory $entry.Path))
    if (!$file.StartsWith($BundleDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Invalid manifest path: $($entry.Path)"
    }
    if (!(Test-Path -LiteralPath $file -PathType Leaf) -or
        (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.SHA256) {
        throw "Bundle verification failed: $($entry.Path)"
    }
}
$managedPath = Join-Path $root '.compressr-portable.json'
$managed = @{}
if ((Test-Path -LiteralPath $managedPath) -and (Get-Item -LiteralPath $managedPath).Length -gt 0) {
    foreach ($entry in (Get-Content -LiteralPath $managedPath -Raw | ConvertFrom-Json)) {
        $managed[$entry.Path] = $entry.SHA256
    }
}
foreach ($process in Get-Process -Name Compressr -ErrorAction SilentlyContinue) {
    if ($process.Path -eq (Join-Path $root 'Compressr.exe')) {
        throw 'Close the repository-root Compressr app, then run .\Build.ps1 again.'
    }
}
$files = Get-ChildItem -LiteralPath $BundleDirectory -Recurse -File | Where-Object {
    $_.FullName -ne (Join-Path $BundleDirectory 'README.md') -and $_.FullName -ne $manifestPath
}
$plan = foreach ($file in $files) {
    $relative = $file.FullName.Substring($BundleDirectory.Length + 1)
    if ($relative -notmatch '^(tools|licenses|cs|de|es|fr|it|ja|ko|pl|pt-BR|ru|tr|zh-Hans|zh-Hant)\\' -and
        $relative -notmatch '^[^\\]+\.(dll|pdb)$' -and
        $relative -notin @('Compressr.exe', 'createdump.exe', 'Compressr.deps.json',
            'Compressr.runtimeconfig.json', 'START HERE.txt')) {
        throw "Unexpected bundle file; source is protected: $relative"
    }
    $target = Join-Path $root $relative
    $parent = Split-Path $target -Parent
    while ($parent -ne $root) {
        if ((Test-Path -LiteralPath $parent) -and !(Test-Path -LiteralPath $parent -PathType Container)) {
            throw "Target directory collides with a file: $parent"
        }
        $parent = Split-Path $parent -Parent
    }
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    $exists = Test-Path -LiteralPath $target
    $oldHash = $null
    if ($exists) {
        if (!(Test-Path -LiteralPath $target -PathType Leaf)) { throw "Target collision: $target" }
        try { $oldHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash }
        catch { throw "Cannot read portable file; close Compressr or the program locking $target, then rebuild. $($_.Exception.Message)" }
        if ($oldHash -ne $hash -and $managed[$relative] -ne $oldHash) {
            throw "Target collision; nothing copied. Relocate the unrecognized or edited portable file first: $target"
        }
    }
    [pscustomobject]@{
        Path = $relative; Source = $file.FullName; Target = $target; SHA256 = $hash
        OldHash = $oldHash; Exists = $exists; Changed = $oldHash -ne $hash
        Stream = $null; Backup = $null; Written = $false
    }
}
$backupRoot = Join-Path $root ('artifacts\placement-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
$lock = $null
$oldManifest = $null
try {
    # One placement at a time; hold every changed existing file exclusively before writing.
    $lock = [IO.File]::Open($managedPath, 'OpenOrCreate', 'ReadWrite', 'None')
    $oldManifest = New-Object byte[] ([int]$lock.Length)
    if ($oldManifest.Length) { $null = $lock.Read($oldManifest, 0, $oldManifest.Length) }
    foreach ($entry in $plan | Where-Object { $_.Changed -and $_.Exists }) {
        try { $entry.Stream = [IO.File]::Open($entry.Target, 'Open', 'ReadWrite', 'None') }
        catch { throw "File locked; close Compressr and other programs using $($entry.Target), then rebuild. $($_.Exception.Message)" }
        $entry.Backup = Join-Path $backupRoot $entry.Path
        New-Item -ItemType Directory -Force -Path (Split-Path $entry.Backup -Parent) | Out-Null
        $backup = [IO.File]::Create($entry.Backup)
        try { $entry.Stream.CopyTo($backup) } finally { $backup.Dispose() }
        if ((Get-FileHash -LiteralPath $entry.Backup).Hash -ne $entry.OldHash) {
            throw "File changed during placement; retry: $($entry.Target)"
        }
    }
    foreach ($entry in $plan | Where-Object Changed) {
        if ($entry.Exists) {
            $entry.Written = $true
            $source = [IO.File]::OpenRead($entry.Source)
            try {
                $entry.Stream.Position = 0
                $source.CopyTo($entry.Stream)
                $entry.Stream.SetLength($source.Length)
                $entry.Stream.Flush()
            } finally { $source.Dispose() }
            $entry.Stream.Position = 0
            $sha = [Security.Cryptography.SHA256]::Create()
            try {
                $actual = [BitConverter]::ToString($sha.ComputeHash($entry.Stream)).Replace('-', '')
                if ($actual -ne $entry.SHA256) { throw "Written file verification failed: $($entry.Target)" }
            } finally { $sha.Dispose() }
        } else {
            New-Item -ItemType Directory -Force -Path (Split-Path $entry.Target -Parent) | Out-Null
            [IO.File]::Copy($entry.Source, $entry.Target, $false)
            $entry.Written = $true
            if ((Get-FileHash -LiteralPath $entry.Target).Hash -ne $entry.SHA256) {
                throw "Written file verification failed: $($entry.Target)"
            }
        }
    }
    $json = @($plan | Select-Object Path, SHA256) | ConvertTo-Json -Depth 3
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $lock.Position = 0
    $lock.Write($bytes, 0, $bytes.Length)
    $lock.SetLength($bytes.Length)
    $lock.Flush()
} catch {
    foreach ($entry in $plan | Where-Object Written) {
        if ($entry.Exists) {
            $backup = [IO.File]::OpenRead($entry.Backup)
            try {
                $entry.Stream.Position = 0
                $backup.CopyTo($entry.Stream)
                $entry.Stream.SetLength($backup.Length)
                $entry.Stream.Flush()
            } finally { $backup.Dispose() }
        } else { Remove-Item -LiteralPath $entry.Target }
    }
    if ($lock -and $null -ne $oldManifest) {
        $lock.Position = 0
        $lock.Write($oldManifest, 0, $oldManifest.Length)
        $lock.SetLength($oldManifest.Length)
        $lock.Flush()
    }
    throw
} finally {
    foreach ($entry in $plan) { if ($entry.Stream) { $entry.Stream.Dispose() } }
    if ($lock) { $lock.Dispose() }
}
Write-Host "Double-click: $(Join-Path $root 'Compressr.exe')"
}
$placementLockPath = Join-Path (Split-Path $PSScriptRoot -Parent) '.compressr-placement.lock'
try { $placementLock = [IO.File]::Open($placementLockPath, 'OpenOrCreate', 'ReadWrite', 'None') }
catch { throw 'Another placement is active. Wait for it to finish, then rebuild.' }
try { Invoke-Placement } finally { $placementLock.Dispose() }
