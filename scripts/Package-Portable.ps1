param([string]$OutputDirectory, [switch]$SkipZip, [switch]$RepositoryRoot)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (!$OutputDirectory) {
    $OutputDirectory = Join-Path $root ("artifacts\portable-" + [guid]::NewGuid().ToString('N') + '\Compressr')
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path $OutputDirectory) { throw 'Choose a new output directory; existing packages are never overwritten.' }
if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') {
    throw 'This personal package targets Windows x64. Build on an x64 Windows machine.'
}
$tools = Join-Path $root 'tools'
foreach ($relative in @('ffmpeg.exe', 'ffprobe.exe', 'yt-dlp.exe', 'deno.exe',
    'DVDStylerPortable\App\DVDStyler\bin\dvdauthor.exe',
    'DVDStylerPortable\App\DVDStyler\bin\mkisofs.exe',
    'DVDStylerPortable\App\DVDStyler\COPYING')) {
    if (!(Test-Path (Join-Path $tools $relative))) {
        throw "Missing $relative. Run scripts\Setup-Tools.ps1 before packaging."
    }
}
$notices = Join-Path $tools 'notices'
New-Item -ItemType Directory -Force $notices | Out-Null
# Cache upstream notices for repeat packaging. Downloads happen here, never at app launch.
$licenses = @{
    'FFmpeg-GPL-3.0.txt' = 'https://raw.githubusercontent.com/FFmpeg/FFmpeg/master/COPYING.GPLv3'
    'yt-dlp-LICENSE.txt' = 'https://raw.githubusercontent.com/yt-dlp/yt-dlp/master/LICENSE'
    'yt-dlp-README.md' = 'https://raw.githubusercontent.com/yt-dlp/yt-dlp/master/README.md'
    'Deno-LICENSE.txt' = 'https://raw.githubusercontent.com/denoland/deno/main/LICENSE.md'
    'dotnet-LICENSE.txt' = 'https://raw.githubusercontent.com/dotnet/runtime/main/LICENSE.TXT'
    'dotnet-THIRD-PARTY-NOTICES.txt' = 'https://raw.githubusercontent.com/dotnet/runtime/main/THIRD-PARTY-NOTICES.TXT'
    'WPF-LICENSE.txt' = 'https://raw.githubusercontent.com/dotnet/wpf/main/LICENSE.TXT'
    'WPF-THIRD-PARTY-NOTICES.txt' = 'https://raw.githubusercontent.com/dotnet/wpf/main/THIRD-PARTY-NOTICES.TXT'
}
foreach ($name in $licenses.Keys) {
    $target = Join-Path $notices $name
    if (!(Test-Path $target)) { Invoke-WebRequest $licenses[$name] -OutFile $target }
    if ((Get-Item $target).Length -eq 0) { throw "Empty upstream notice: $name" }
}
& dotnet publish (Join-Path $root 'Compressr.App\Compressr.App.csproj') -c Release -r win-x64 `
    --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o $OutputDirectory --nologo
if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
$bundleTools = Join-Path $OutputDirectory 'tools'
New-Item -ItemType Directory $bundleTools | Out-Null
foreach ($name in @('ffmpeg.exe', 'ffprobe.exe', 'yt-dlp.exe', 'deno.exe')) {
    Copy-Item (Join-Path $tools $name) $bundleTools
}
# Keep the original authoring package layout, DLLs and upstream license/source notices.
Copy-Item (Join-Path $tools 'DVDStylerPortable') $bundleTools -Recurse
Copy-Item $notices (Join-Path $OutputDirectory 'licenses') -Recurse
Copy-Item (Join-Path $root 'scripts\Portable-Quick-Start.txt') (Join-Path $OutputDirectory 'START HERE.txt')
Copy-Item (Join-Path $root 'README.md') $OutputDirectory
$manifest = Get-ChildItem $OutputDirectory -Recurse -File | ForEach-Object {
    [pscustomobject]@{
        Path = $_.FullName.Substring($OutputDirectory.TrimEnd('\').Length + 1)
        Bytes = $_.Length
        SHA256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
    }
}
$manifest | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $OutputDirectory 'bundle-manifest.json') -Encoding utf8
if (!$SkipZip) {
    $zip = "$OutputDirectory-win-x64.zip"
    if (Test-Path $zip) { throw "ZIP already exists: $zip" }
    Compress-Archive -LiteralPath $OutputDirectory -DestinationPath $zip -CompressionLevel Optimal
    Write-Host "ZIP: $zip"
}
Write-Host "Double-click: $(Join-Path $OutputDirectory 'Compressr.exe')"
if ($RepositoryRoot) {
    & (Join-Path $PSScriptRoot 'Place-Portable.ps1') -BundleDirectory $OutputDirectory
}
