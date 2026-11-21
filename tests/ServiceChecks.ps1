$ErrorActionPreference = 'Stop'

function Invoke-ControlTime($Command, $Password, $Settings, $Delay = 0) {
    $rights = [System.IO.Pipes.PipeAccessRights]'ReadWrite, ReadPermissions, Synchronize'
    $request = @{ Command = $Command; Password = $Password; Settings = $Settings } |
        ConvertTo-Json -Depth 6 -Compress
    $request = [Text.Encoding]::UTF8.GetBytes($request)
    $timeout = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(5))
    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(
        '.', 'ControlTime.Parent.v2', $rights,
        [System.IO.Pipes.PipeOptions]::Asynchronous,
        [System.Security.Principal.TokenImpersonationLevel]::Anonymous,
        [System.IO.HandleInheritability]::None)

    try {
        $pipe.ConnectAsync($timeout.Token).GetAwaiter().GetResult()
        $acl = [System.IO.Pipes.PipesAclExtensions]::GetAccessControl($pipe)
        if ($acl.GetOwner([System.Security.Principal.SecurityIdentifier]).Value -ne 'S-1-5-18') {
            throw 'The pipe is not owned by LocalSystem'
        }

        $header = [BitConverter]::GetBytes([int]$request.Length)
        $pipe.Write($header, 0, $header.Length)
        $pipe.Write($request, 0, $request.Length)
        Start-Sleep -Milliseconds $Delay
        $pipe.ReadExactlyAsync($header, 0, $header.Length, $timeout.Token).AsTask().GetAwaiter().GetResult()
        $length = [BitConverter]::ToInt32($header, 0)
        if ($length -lt 1 -or $length -gt 65536) { throw "Invalid response size: $length" }
        $reply = [byte[]]::new($length)
        $pipe.ReadExactlyAsync($reply, 0, $reply.Length, $timeout.Token).AsTask().GetAwaiter().GetResult()
        return [Text.Encoding]::UTF8.GetString($reply) | ConvertFrom-Json
    } finally {
        $pipe.Dispose()
        $timeout.Dispose()
    }
}

try {
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        $status = Invoke-ControlTime -Command status -Delay 300
        if ($status.Configured -ne $false -or $status.Error -or $status.Message -ne 'Задайте отдельный пароль родителя.') {
            throw "Expected setup prompt before configuration: $($status.Message) $($status.Error)"
        }
    }

    # Create the same protected on-disk profile as the setup dialog on this disposable runner.
    $installPath = Join-Path $env:ProgramFiles 'ControlTime'
    $privatePath = Join-Path $installPath 'private'
    New-Item -ItemType Directory -Path $privatePath | Out-Null
    $acl = [System.Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in 'S-1-5-18', 'S-1-5-32-544') {
        $rule = [System.Security.AccessControl.FileSystemAccessRule]::new(
            [System.Security.Principal.SecurityIdentifier]::new($sid),
            'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $acl.AddAccessRule($rule)
    }
    Set-Acl -Path $privatePath -AclObject $acl
    $password = [Guid]::NewGuid().ToString()
    $salt = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(16)
    $hash = [System.Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2(
        $password, $salt, 600000, [System.Security.Cryptography.HashAlgorithmName]::SHA256, 32)
    $settings = @{
        Sessions = @(@{ UseMinutes = 60; BreakMinutes = 180 }, @{ UseMinutes = 60; BreakMinutes = 0 })
        QuietHours = @()
        ResetRequest = [Guid]::Empty.ToString()
        TimeZoneId = 'UTC'
    }
    $profile = @{
        Settings = $settings
        Password = @{ Salt = [Convert]::ToBase64String($salt); Hash = [Convert]::ToBase64String($hash) }
    }
    $profilePath = Join-Path $privatePath 'profile.json'
    $profile | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $profilePath -Encoding utf8NoBOM

    $usagePath = Join-Path $installPath 'usage.json'
    for ($attempt = 1; $attempt -le 10; $attempt++) {
        $status = Invoke-ControlTime -Command status
        if ($status.Configured -and $status.Settings -and (Test-Path $usagePath)) { break }
        Start-Sleep -Milliseconds 300
    }
    if (-not $status.Configured -or $status.Error -or $status.Blocked -or
        $status.Settings.Sessions[0].UseMinutes -ne 60 -or -not (Test-Path $usagePath)) {
        throw "Settings or accounting not available after setup: $($status.Message) $($status.Error)"
    }

    $status = Invoke-ControlTime -Command parent -Password $password
    if (-not $status.ParentMode -or $status.Error) {
        throw "Parent mode unavailable: $($status.Message) $($status.Error)"
    }
    $secondId = [Guid]::NewGuid().ToString()
    $settings.Scenarios = @(
        @{ Id = [Guid]::Empty.ToString(); Name = 'Original'; Sessions = $settings.Sessions },
        @{ Id = $secondId; Name = 'Weekend'; Sessions = @(@{ UseMinutes = 45; BreakMinutes = 0 }) }
    )
    $settings.ActiveScenarioId = $secondId
    $settings.ShutDownOnLimit = $false
    $settings.DailyShutdownMinute = 1320
    $status = Invoke-ControlTime -Command save -Password $password -Settings $settings
    if ($status.Error -or $status.Settings.Sessions[0].UseMinutes -ne 45) {
        throw "Active scenario not applied: $($status.Message) $($status.Error)"
    }
    $saved = Get-Content -Raw -LiteralPath $profilePath | ConvertFrom-Json
    if ($saved.Settings.Scenarios.Count -ne 2 -or $saved.Settings.ActiveScenarioId -ne $secondId -or
        $saved.Settings.Sessions[0].UseMinutes -ne 45 -or $saved.Settings.ShutDownOnLimit -or
        $saved.Settings.DailyShutdownMinute -ne 1320 -or $saved.Settings.ResetRequest -eq [Guid]::Empty.ToString()) {
        throw 'Scenarios, reset or shutdown preferences were not persisted'
    }
    $revision = $saved.Settings.ResetRequest
    $settings.Scenarios[1].Name = 'Weekend renamed'
    $status = Invoke-ControlTime -Command save -Password $password -Settings $settings
    if ($status.Error -or $status.Settings.ResetRequest -ne $revision) {
        throw 'Renaming the active scenario should keep daily progress'
    }
} catch {
    Write-Host "::error::$($_.Exception.GetBaseException().Message)"
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'ControlTime' } -MaxEvents 3 -ErrorAction SilentlyContinue |
        ForEach-Object { Write-Host "::notice::$($_.Message)" }
    exit 1
}
