param([switch]$Integration, [string]$YouTubeUrl)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    & dotnet build 'Compressr.App\Compressr.App.csproj' -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed.' }
    $arguments = @('run', '--project', 'Compressr.Tests\Compressr.Tests.csproj', '-c', 'Release', '--')
    $runDirectory = Join-Path $root ("artifacts\run-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    if ($Integration) { $arguments += @('--integration', '--output', $runDirectory) }
    if ($YouTubeUrl) {
        if (!$Integration) { throw '-YouTubeUrl requires -Integration.' }
        $arguments += @('--youtube', $YouTubeUrl)
    }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    if ($Integration) {
        # Read the generated image itself, not just the pre-image authoring folder.
        foreach ($iso in Get-ChildItem $runDirectory -Filter '*.iso') {
            $disk = Mount-DiskImage -ImagePath $iso.FullName -PassThru
            try {
                $volume = $disk | Get-Volume
                for ($attempt = 0; $attempt -lt 40 -and !$volume.DriveLetter; $attempt++) {
                    Start-Sleep -Milliseconds 250
                    $volume = $disk | Get-Volume
                }
                if (!$volume.DriveLetter) { throw "Image has no mounted drive: $($iso.Name)" }
                $videoTs = "$($volume.DriveLetter):\VIDEO_TS"
                if (!(Test-Path (Join-Path $videoTs 'VIDEO_TS.IFO'))) { throw 'Mounted image is missing DVD navigation.' }
                $expected = Join-Path $runDirectory "$($iso.BaseName)\dvd\VIDEO_TS"
                foreach ($file in Get-ChildItem $expected -File) {
                    $copy = Join-Path $videoTs $file.Name
                    if ((Get-FileHash $file.FullName).Hash -ne (Get-FileHash $copy).Hash) { throw "ISO file mismatch: $copy" }
                }
                Write-Host "PASS: mounted $($iso.Name), VIDEO_TS read-back hashes match"
            } finally { Dismount-DiskImage -ImagePath $iso.FullName | Out-Null }
        }
    }
} finally { Pop-Location }
