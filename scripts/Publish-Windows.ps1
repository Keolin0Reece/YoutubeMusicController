[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = Join-Path $repositoryRoot 'artifacts'
$serviceOutput = Join-Path $artifactsRoot 'service'
$trayOutput = Join-Path $artifactsRoot 'tray'

Push-Location $repositoryRoot

try {
    dotnet publish `
        '.\YoutubeMusicController.Api\YoutubeMusicController.Api.csproj' `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -o $serviceOutput

    if ($LASTEXITCODE -ne 0) {
        throw "Service publish failed with exit code $LASTEXITCODE."
    }

    dotnet publish `
        '.\YoutubeMusicController.Tray\YoutubeMusicController.Tray.csproj' `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -o $trayOutput

    if ($LASTEXITCODE -ne 0) {
        throw "Tray publish failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

Write-Host "Service published to $serviceOutput"
Write-Host "Tray app published to $trayOutput"
