$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
& dotnet run --project (Join-Path $root 'Compressr.App\Compressr.App.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw "Compressr exited with code $LASTEXITCODE" }
