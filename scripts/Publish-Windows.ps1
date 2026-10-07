[CmdletBinding()]
param(
    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = Join-Path $repositoryRoot 'artifacts'
$controllerOutput = Join-Path $artifactsRoot 'controller'
$trayOutput = Join-Path $artifactsRoot 'tray'

$resolvedArtifactsRoot = [IO.Path]::GetFullPath($artifactsRoot).TrimEnd(
    [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

foreach ($outputDirectory in @($controllerOutput, $trayOutput)) {
    $resolvedOutput = [IO.Path]::GetFullPath($outputDirectory)

    if (-not $resolvedOutput.StartsWith(
            $resolvedArtifactsRoot,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean output outside the artifacts directory: $resolvedOutput"
    }

    if (Test-Path -LiteralPath $resolvedOutput) {
        Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
    }
}

Push-Location $repositoryRoot

try {
    Push-Location '.\YoutubeMusicController.Web'

    try {
        npm ci

        if ($LASTEXITCODE -ne 0) {
            throw "Web dependency restore failed with exit code $LASTEXITCODE."
        }

        npm run build

        if ($LASTEXITCODE -ne 0) {
            throw "Web build failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }

    dotnet publish `
        '.\YoutubeMusicController.Api\YoutubeMusicController.Api.csproj' `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:Version=$Version `
        -o $controllerOutput

    if ($LASTEXITCODE -ne 0) {
        throw "Controller publish failed with exit code $LASTEXITCODE."
    }

    $webOutput = Join-Path $repositoryRoot 'YoutubeMusicController.Web\dist'
    $webDestination = Join-Path $controllerOutput 'wwwroot'
    New-Item -ItemType Directory -Path $webDestination -Force | Out-Null
    Copy-Item -Path (Join-Path $webOutput '*') `
        -Destination $webDestination `
        -Recurse `
        -Force

    dotnet publish `
        '.\YoutubeMusicController.Tray\YoutubeMusicController.Tray.csproj' `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:Version=$Version `
        -o $trayOutput

    if ($LASTEXITCODE -ne 0) {
        throw "Tray publish failed with exit code $LASTEXITCODE."
    }

    Get-ChildItem -LiteralPath $controllerOutput -Filter '*.pdb' |
        Remove-Item -Force
    Get-ChildItem -LiteralPath $trayOutput -Filter '*.pdb' |
        Remove-Item -Force

    foreach ($developmentFile in @(
            (Join-Path $controllerOutput 'appsettings.Development.json')
            (Join-Path $controllerOutput 'web.config')
            (Join-Path $controllerOutput 'aspnetcorev2_inprocess.dll')
            (Join-Path $controllerOutput 'YoutubeMusicController.Api.staticwebassets.endpoints.json'))) {
        if (Test-Path -LiteralPath $developmentFile) {
            Remove-Item -LiteralPath $developmentFile -Force
        }
    }
}
finally {
    Pop-Location
}

Write-Host "Controller published to $controllerOutput"
Write-Host "Tray app published to $trayOutput"
