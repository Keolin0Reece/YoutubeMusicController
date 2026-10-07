[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

function Read-BotToken {
    $form = [Windows.Forms.Form]::new()
    $form.Text = 'Configure Discord Bot Token'
    $form.ClientSize = [Drawing.Size]::new(560, 145)
    $form.FormBorderStyle = [Windows.Forms.FormBorderStyle]::FixedDialog
    $form.StartPosition = [Windows.Forms.FormStartPosition]::CenterScreen
    $form.MaximizeBox = $false
    $form.MinimizeBox = $false
    $form.TopMost = $true

    $label = [Windows.Forms.Label]::new()
    $label.AutoSize = $true
    $label.Location = [Drawing.Point]::new(16, 16)
    $label.Text = 'Paste the Bot Token from Developer Portal > Bot > Reset Token:'
    $form.Controls.Add($label)

    $textBox = [Windows.Forms.TextBox]::new()
    $textBox.Location = [Drawing.Point]::new(16, 46)
    $textBox.Size = [Drawing.Size]::new(528, 24)
    $textBox.UseSystemPasswordChar = $true
    $form.Controls.Add($textBox)

    $okButton = [Windows.Forms.Button]::new()
    $okButton.Location = [Drawing.Point]::new(363, 94)
    $okButton.Size = [Drawing.Size]::new(85, 30)
    $okButton.Text = 'Save'
    $okButton.DialogResult = [Windows.Forms.DialogResult]::OK
    $form.AcceptButton = $okButton
    $form.Controls.Add($okButton)

    $cancelButton = [Windows.Forms.Button]::new()
    $cancelButton.Location = [Drawing.Point]::new(459, 94)
    $cancelButton.Size = [Drawing.Size]::new(85, 30)
    $cancelButton.Text = 'Cancel'
    $cancelButton.DialogResult = [Windows.Forms.DialogResult]::Cancel
    $form.CancelButton = $cancelButton
    $form.Controls.Add($cancelButton)

    $form.Add_Shown({ $textBox.Focus() })

    try {
        if ($form.ShowDialog() -ne [Windows.Forms.DialogResult]::OK) {
            throw 'Discord configuration was cancelled.'
        }

        return $textBox.Text.Trim()
    }
    finally {
        $form.Dispose()
    }
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$isAdministrator = $principal.IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdministrator) {
    $elevated = Start-Process `
        -FilePath 'powershell.exe' `
        -ArgumentList @(
            '-NoProfile'
            '-ExecutionPolicy'
            'Bypass'
            '-File'
            "`"$PSCommandPath`""
        ) `
        -Verb RunAs `
        -Wait `
        -PassThru

    exit $elevated.ExitCode
}

do {
    $applicationIdText = Read-Host 'Discord Application ID'
    $applicationId = 0L
    $validApplicationId = [UInt64]::TryParse(
        $applicationIdText,
        [ref]$applicationId)

    if (-not $validApplicationId -or $applicationId -eq 0) {
        Write-Warning 'Enter the numeric Application ID from General Information.'
    }
} until ($validApplicationId -and $applicationId -ne 0)

$guildIdText = Read-Host 'Development Server (Guild) ID, or press Enter for global commands'
$guildId = $null

if (-not [string]::IsNullOrWhiteSpace($guildIdText)) {
    $parsedGuildId = 0L

    if (-not [UInt64]::TryParse($guildIdText, [ref]$parsedGuildId) -or
        $parsedGuildId -eq 0) {
        throw 'The Guild ID must be a numeric Discord server ID.'
    }

    $guildId = $parsedGuildId
}

$botToken = $null
$plaintext = $null
$encrypted = $null

try {
    $botToken = Read-BotToken

    if ([string]::IsNullOrWhiteSpace($botToken)) {
        throw 'The Bot Token cannot be empty.'
    }

    if ($botToken.Length -lt 58) {
        throw 'That value is too short to be a Discord Bot Token. In the Developer Portal, open Bot, click Reset Token, and copy the newly displayed token. Do not use the Public Key or Client Secret.'
    }

    $state = [ordered]@{
        ApplicationId = $applicationId
        GuildId = $guildId
        BotToken = $botToken
    }
    $json = $state | ConvertTo-Json -Compress
    $plaintext = [Text.Encoding]::UTF8.GetBytes($json)
    $entropy = [Security.Cryptography.SHA256]::Create().ComputeHash(
        [Text.Encoding]::UTF8.GetBytes(
            'YoutubeMusicController.DiscordAuthorization.v1'))
    $encrypted = [Security.Cryptography.ProtectedData]::Protect(
        $plaintext,
        $entropy,
        [Security.Cryptography.DataProtectionScope]::LocalMachine)

    $dataDirectory = Join-Path $env:ProgramData 'YoutubeMusicController'
    $tokenPath = Join-Path $dataDirectory 'discord-auth.dat'
    New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null

    $allowedSids = @(
        [Security.Principal.SecurityIdentifier]::new(
            [Security.Principal.WellKnownSidType]::LocalSystemSid,
            $null)
        [Security.Principal.SecurityIdentifier]::new(
            [Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid,
            $null)
        [Security.Principal.WindowsIdentity]::GetCurrent().User
    ) | Select-Object -Unique

    $directorySecurity = [Security.AccessControl.DirectorySecurity]::new()
    $directorySecurity.SetAccessRuleProtection($true, $false)

    foreach ($sid in $allowedSids) {
        $directorySecurity.AddAccessRule(
            [Security.AccessControl.FileSystemAccessRule]::new(
                $sid,
                [Security.AccessControl.FileSystemRights]::FullControl,
                [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit',
                [Security.AccessControl.PropagationFlags]::None,
                [Security.AccessControl.AccessControlType]::Allow))
    }

    [IO.Directory]::SetAccessControl($dataDirectory, $directorySecurity)
    [IO.File]::WriteAllBytes($tokenPath, $encrypted)

    $fileSecurity = [Security.AccessControl.FileSecurity]::new()
    $fileSecurity.SetAccessRuleProtection($true, $false)

    foreach ($sid in $allowedSids) {
        $fileSecurity.AddAccessRule(
            [Security.AccessControl.FileSystemAccessRule]::new(
                $sid,
                [Security.AccessControl.FileSystemRights]::FullControl,
                [Security.AccessControl.AccessControlType]::Allow))
    }

    [IO.File]::SetAccessControl($tokenPath, $fileSecurity)

    $service = Get-Service -Name 'YoutubeMusicController' -ErrorAction SilentlyContinue

    if ($service -and $service.Status -ne 'Stopped') {
        Restart-Service -Name 'YoutubeMusicController'
        $service.WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
        Write-Host 'Discord configuration saved and the controller restarted.'
    }
    else {
        Write-Host 'Discord configuration saved. Start the controller when ready.'
    }

    Write-Host 'Token store:' $tokenPath
}
catch {
    Write-Host ''
    Write-Host 'Discord configuration failed:' -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    Read-Host 'Press Enter to close this window'
    exit 1
}
finally {
    $botToken = $null

    if ($plaintext) {
        [Array]::Clear($plaintext, 0, $plaintext.Length)
    }

    if ($encrypted) {
        [Array]::Clear($encrypted, 0, $encrypted.Length)
    }
}
