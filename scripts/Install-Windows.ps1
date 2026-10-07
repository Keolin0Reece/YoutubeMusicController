[CmdletBinding()]
param(
    [switch]$StartService
)

$ErrorActionPreference = 'Stop'
$serviceName = 'YoutubeMusicController'
$displayName = 'YouTube Music Controller'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$serviceSource = Join-Path $repositoryRoot 'artifacts\service'
$traySource = Join-Path $repositoryRoot 'artifacts\tray'
$installRoot = Join-Path $env:ProgramFiles 'YoutubeMusicController'
$serviceDirectory = Join-Path $installRoot 'Service'
$trayDirectory = Join-Path $installRoot 'Tray'
$serviceExecutable = Join-Path $serviceDirectory 'YoutubeMusicController.Api.exe'
$trayExecutable = Join-Path $trayDirectory 'YoutubeMusicController.Tray.exe'
$discordSetupSource = Join-Path $repositoryRoot 'scripts\Configure-Discord.ps1'
$discordSetupDestination = Join-Path $installRoot 'Configure-Discord.ps1'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$isAdministrator = $principal.IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdministrator) {
    $shellPath = (Get-Process -Id $PID).Path
    $arguments = @(
        '-NoProfile'
        '-ExecutionPolicy'
        'Bypass'
        '-File'
        "`"$PSCommandPath`""
    )

    if ($StartService) {
        $arguments += '-StartService'
    }

    $arguments = $arguments -join ' '

    $elevated = Start-Process `
        -FilePath $shellPath `
        -ArgumentList $arguments `
        -Verb RunAs `
        -Wait `
        -PassThru

    exit $elevated.ExitCode
}

if (-not (Test-Path -LiteralPath $serviceSource -PathType Container)) {
    throw "Published service not found at $serviceSource. Run Publish-Windows.ps1 first."
}

if (-not (Test-Path -LiteralPath $traySource -PathType Container)) {
    throw "Published tray app not found at $traySource. Run Publish-Windows.ps1 first."
}

$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
$wasRunning = $StartService -or ($existingService -and
    $existingService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped)

if ($existingService -and
    $existingService.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
    $serviceProcessId = (Get-CimInstance Win32_Service `
        -Filter "Name='$serviceName'").ProcessId
    Stop-Service -Name $serviceName
    $existingService.WaitForStatus(
        [System.ServiceProcess.ServiceControllerStatus]::Stopped,
        [TimeSpan]::FromSeconds(30))

    if ($serviceProcessId) {
        $serviceProcess = Get-Process `
            -Id $serviceProcessId `
            -ErrorAction SilentlyContinue

        if ($serviceProcess) {
            $serviceProcess.WaitForExit(10000)
        }
    }
}

Get-Process -Name 'YoutubeMusicController.Tray' -ErrorAction SilentlyContinue |
    Stop-Process -Force

New-Item -ItemType Directory -Path $serviceDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $trayDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $serviceSource '*') -Destination $serviceDirectory -Recurse -Force
Copy-Item -Path (Join-Path $traySource '*') -Destination $trayDirectory -Recurse -Force
Copy-Item -LiteralPath $discordSetupSource -Destination $discordSetupDestination -Force

$binaryPath = (
    "`"$serviceExecutable`" " +
    "--contentRoot `"$serviceDirectory`" " +
    '--urls http://127.0.0.1:5161'
)

if (-not $existingService) {
    New-Service `
        -Name $serviceName `
        -BinaryPathName $binaryPath `
        -DisplayName $displayName `
        -Description 'Controls YouTube Music through Twitch and Discord.' `
        -StartupType Manual | Out-Null
}
else {
    $scArguments = @(
        'config'
        $serviceName
        'binPath='
        $binaryPath
        'start='
        'demand'
        'obj='
        'LocalSystem'
        'DisplayName='
        $displayName
    )

    & sc.exe @scArguments | Out-Host

    if ($LASTEXITCODE -ne 0) {
        throw "Unable to update the Windows Service configuration. sc.exe exited with $LASTEXITCODE."
    }

    & sc.exe description $serviceName 'Controls YouTube Music through Twitch and Discord.' | Out-Host

    if ($LASTEXITCODE -ne 0) {
        throw "Unable to update the Windows Service description. sc.exe exited with $LASTEXITCODE."
    }
}

$startupDirectory = [Environment]::GetFolderPath('Startup')
$shortcutPath = Join-Path $startupDirectory 'YouTube Music Controller.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $trayExecutable
$shortcut.WorkingDirectory = $trayDirectory
$shortcut.Description = 'YouTube Music Controller tray controls'
$shortcut.Save()

if ($wasRunning) {
    Start-Service -Name $serviceName
}

Write-Host "Installed service: $serviceExecutable"
Write-Host "Installed tray app: $trayExecutable"
Write-Host "Installed Discord setup: $discordSetupDestination"
Write-Host "Created startup shortcut: $shortcutPath"
Write-Host 'Start the tray app from the Start menu shortcut or sign out and back in.'
