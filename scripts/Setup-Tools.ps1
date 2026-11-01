param([switch]$UpdateYouTube)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$tools = Join-Path $root 'tools'
New-Item -ItemType Directory -Force $tools | Out-Null

if ($UpdateYouTube -or !(Test-Path (Join-Path $tools 'yt-dlp.exe'))) {
    Invoke-WebRequest 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe' -OutFile (Join-Path $tools 'yt-dlp.exe')
}
if (!(Test-Path (Join-Path $tools 'deno.exe'))) {
    $denoArchive = Join-Path $tools 'deno.zip'
    Invoke-WebRequest 'https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip' -OutFile $denoArchive
    Expand-Archive $denoArchive $tools -Force
    Remove-Item $denoArchive
}
if (!(Test-Path (Join-Path $tools 'ffmpeg.exe')) -or !(Test-Path (Join-Path $tools 'ffprobe.exe'))) {
    $archive = Join-Path $tools 'ffmpeg-download.zip'
    $extract = Join-Path $tools 'ffmpeg-download'
    Invoke-WebRequest 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip' -OutFile $archive
    Expand-Archive $archive $extract -Force
    $bin = Get-ChildItem $extract -Filter ffmpeg.exe -Recurse | Select-Object -First 1
    if (!$bin) { throw 'FFmpeg archive did not contain ffmpeg.exe.' }
    Copy-Item $bin.FullName (Join-Path $tools 'ffmpeg.exe')
    Copy-Item (Join-Path $bin.DirectoryName 'ffprobe.exe') (Join-Path $tools 'ffprobe.exe')
    Remove-Item $archive
}

# The portable package supplies native dvdauthor/mkisofs with their runtime DLLs.
# Pin and verify the PortableApps-published SHA-256; unpack without running the installer.
$dvdBin = Join-Path $tools 'DVDStylerPortable\App\DVDStyler\bin'
if (!(Test-Path (Join-Path $dvdBin 'dvdauthor.exe')) -or
    !(Test-Path (Join-Path $dvdBin 'mkisofs.exe')) -or
    !(Test-Path (Join-Path $tools 'DVDStylerPortable\App\DVDStyler\COPYING'))) {
    $package = Join-Path $tools 'DVDStylerPortable_3.2.1.paf.exe'
    Invoke-WebRequest 'https://portableapps.com/redir2/?a=DVDStylerPortable&s=s&d=pa&f=DVDStylerPortable_3.2.1.paf.exe' -OutFile $package
    $expected = 'cd52e3c2b7e475f9dcc84e9e4b25e4fd2e9f7753ba705c24fc8cbf0a4b32db82'
    if ((Get-FileHash $package -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) {
        throw 'DVDStyler portable package checksum mismatch. It has not been executed.'
    }
    $sevenZip = Join-Path $tools '7zip\Files\7-Zip\7z.exe'
    if (!(Test-Path $sevenZip)) {
        $msi = Join-Path $tools '7zip.msi'
        Invoke-WebRequest 'https://www.7-zip.org/a/7z2409-x64.msi' -OutFile $msi
        # Administrative extraction writes files under tools; it does not install 7-Zip.
        $target = Join-Path $tools '7zip'
        $process = Start-Process msiexec.exe -ArgumentList "/a `"$msi`" /qn TARGETDIR=`"$target`"" -PassThru -Wait
        if ($process.ExitCode -ne 0) { throw "7-Zip extraction failed ($($process.ExitCode))." }
        Remove-Item $msi
    }
    & $sevenZip x $package "-o$tools\DVDStylerPortable" -y -bso0 -bsp0
    if ($LASTEXITCODE -ne 0) { throw "Portable package extraction failed ($LASTEXITCODE)." }
    Remove-Item $package
}
foreach ($name in @('dvdauthor.exe', 'mkisofs.exe')) {
    if (!(Test-Path (Join-Path $dvdBin $name))) { throw "Required tool missing: $name in $dvdBin" }
}
& (Join-Path $tools 'ffmpeg.exe') -version | Select-Object -First 1
& (Join-Path $tools 'yt-dlp.exe') --version
$previousPreference = $ErrorActionPreference
try {
    # dvdauthor prints help to stderr and deliberately returns 1, including on Windows PowerShell.
    $ErrorActionPreference = 'Continue'
    $help = & (Join-Path $dvdBin 'dvdauthor.exe') -h 2>&1
    $helpExit = $LASTEXITCODE
} finally { $ErrorActionPreference = $previousPreference }
if ($helpExit -ne 1 -or !($help -match 'DVDAuthor::dvdauthor')) { throw 'Bundled dvdauthor could not start.' }
$help | Select-Object -First 3 | ForEach-Object { Write-Host "$_" }
& (Join-Path $dvdBin 'mkisofs.exe') -version
Write-Host 'Tools ready. No third-party binaries are tracked by Git.'
