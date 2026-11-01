$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$scratch = Join-Path $root ('artifacts\placement-test-' + [guid]::NewGuid().ToString('N'))
$bundle = Join-Path $scratch 'bundle'
$checkout = Join-Path $scratch 'checkout'
$script = Join-Path $checkout 'scripts\Place-Portable.ps1'
New-Item -ItemType Directory -Force -Path (Split-Path $script -Parent) | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Place-Portable.ps1') -Destination $script
function Write-Manifest {
    $entries = Get-ChildItem -LiteralPath $bundle -Recurse -File | Where-Object Name -ne 'bundle-manifest.json' |
        ForEach-Object {
            [pscustomobject]@{
                Path = $_.FullName.Substring($bundle.Length + 1)
                SHA256 = (Get-FileHash -LiteralPath $_.FullName).Hash
            }
        }
    $entries | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $bundle 'bundle-manifest.json') -Encoding UTF8
}
function Expect-Failure([string]$pattern) {
    $failed = $false
    try { & $script -BundleDirectory $bundle }
    catch {
        if ($_.Exception.Message -notlike $pattern) { throw }
        $failed = $true
    }
    if (!$failed) { throw "Expected failure: $pattern" }
}
try {
    foreach ($relative in @('Compressr.exe', 'Compressr.dll', 'coreclr.dll', 'hostfxr.dll',
        'Compressr.runtimeconfig.json', 'tools\ffmpeg.exe', 'tools\ffprobe.exe',
        'tools\yt-dlp.exe', 'tools\deno.exe',
        'tools\DVDStylerPortable\App\DVDStyler\bin\dvdauthor.exe',
        'tools\DVDStylerPortable\App\DVDStyler\bin\mkisofs.exe')) {
        $file = Join-Path $bundle $relative
        New-Item -ItemType Directory -Force -Path (Split-Path $file -Parent) | Out-Null
        Set-Content -LiteralPath $file -Value 'first version'
    }
    Set-Content -LiteralPath (Join-Path $checkout 'README.md') -Value 'source README'
    Set-Content -LiteralPath (Join-Path $bundle 'README.md') -Value 'bundle README'
    Write-Manifest
    & $script -BundleDirectory $bundle
    & $script -BundleDirectory $bundle
    if ((Get-Content -LiteralPath (Join-Path $checkout 'README.md')) -ne 'source README') {
        throw 'Source README was overwritten.'
    }
    Set-Content -LiteralPath (Join-Path $bundle 'Compressr.dll') -Value 'second version'
    Write-Manifest
    & $script -BundleDirectory $bundle
    $target = Join-Path $checkout 'Compressr.dll'
    if ((Get-Content -LiteralPath $target) -ne 'second version') { throw 'Managed replacement failed.' }
    $before = (Get-FileHash -LiteralPath (Join-Path $checkout 'Compressr.exe')).Hash
    Set-Content -LiteralPath $target -Value 'user edit'
    Set-Content -LiteralPath (Join-Path $bundle 'Compressr.exe') -Value 'new app'
    Write-Manifest
    Expect-Failure 'Target collision; nothing copied.*'
    if ((Get-Content -LiteralPath $target) -ne 'user edit' -or
        (Get-FileHash -LiteralPath (Join-Path $checkout 'Compressr.exe')).Hash -ne $before) {
        throw 'Collision changed an existing file.'
    }
    Set-Content -LiteralPath $target -Value 'second version'
    $locked = [IO.File]::Open($target, 'Open', 'ReadWrite', 'None')
    try { Expect-Failure 'Cannot read portable file; close Compressr*' }
    finally { $locked.Dispose() }
    if ((Get-FileHash -LiteralPath (Join-Path $checkout 'Compressr.exe')).Hash -ne $before) {
        throw 'Locked-file rejection changed the app.'
    }
    & $script -BundleDirectory $bundle
    Write-Host 'PASS: first placement, repeat, managed update, README preservation, collision and locked-file safety.'
} finally { Remove-Item -LiteralPath $scratch -Recurse }
