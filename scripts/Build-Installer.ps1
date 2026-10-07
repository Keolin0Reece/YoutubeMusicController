[CmdletBinding()]
param(
    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$installerScript = Join-Path $repositoryRoot 'installer\YoutubeMusicController.iss'
$compiler = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue

if (-not $compiler) {
    $candidatePaths = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
    )

    foreach ($candidate in $candidatePaths) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            $compiler = Get-Item -LiteralPath $candidate
            break
        }
    }
}

if (-not $compiler) {
    throw 'Inno Setup 6 is required. Install it, then run this script again.'
}

$compilerPath = if ($compiler.Source) {
    $compiler.Source
} else {
    $compiler.FullName
}

& $compilerPath "/DMyAppVersion=$Version" $installerScript

if ($LASTEXITCODE -ne 0) {
    throw "Installer compilation failed with exit code $LASTEXITCODE."
}

Write-Host "Installer created under $(Join-Path $repositoryRoot 'artifacts\installer')"
