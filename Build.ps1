$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT' -or $env:PROCESSOR_ARCHITECTURE -ne 'AMD64') {
    throw 'Build Compressr on Windows x64.'
}
if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Install the .NET 8 SDK for Windows x64, reopen PowerShell, then run .\Build.ps1.'
}
$sdks = & dotnet --list-sdks
if ($LASTEXITCODE -ne 0 -or !($sdks -match '^8\.0\.')) {
    throw 'Install the .NET 8 SDK for Windows x64, then run .\Build.ps1.'
}
Push-Location $PSScriptRoot
try {
    $tools = Join-Path $PSScriptRoot 'tools'
    $required = @('ffmpeg.exe', 'ffprobe.exe', 'yt-dlp.exe', 'deno.exe',
        'DVDStylerPortable\App\DVDStyler\bin\dvdauthor.exe',
        'DVDStylerPortable\App\DVDStyler\bin\mkisofs.exe',
        'DVDStylerPortable\App\DVDStyler\COPYING')
    if (@($required | Where-Object { !(Test-Path -LiteralPath (Join-Path $tools $_) -PathType Leaf) }).Count) {
        & (Join-Path $PSScriptRoot 'scripts\Setup-Tools.ps1')
    }
    & (Join-Path $PSScriptRoot 'scripts\Package-Portable.ps1') -SkipZip -RepositoryRoot
} finally { Pop-Location }
