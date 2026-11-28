#Requires -Version 5.1

function Get-QmEverythingTaskXml {
    param([string]$UserId, [string]$Root, [string]$Launcher, [string]$EverythingExe, [datetime]$Today)

    $user = [Security.SecurityElement]::Escape($UserId)
    # 载体必须是 WinExe。powershell.exe 会创建控制台，本机默认终端是 Windows Terminal，
    # 移交时新开可见窗口，-WindowStyle Hidden 拦不住（隐藏发生在窗口创建之后）。
    $launcherExe = [Security.SecurityElement]::Escape($Launcher)
    $args = [Security.SecurityElement]::Escape(
        ('--everything-window -Root "{0}" -EverythingExe "{1}"' -f $Root, $EverythingExe))
    $boundary = $Today.Date.AddDays(-1).ToString('yyyy-MM-ddTHH:mm:ss', [Globalization.CultureInfo]::InvariantCulture)
    @"
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <Triggers>
    <TimeTrigger>
      <Repetition><Interval>PT1M</Interval></Repetition>
      <StartBoundary>$boundary</StartBoundary>
    </TimeTrigger>
    <LogonTrigger><UserId>$user</UserId></LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>$user</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <StartWhenAvailable>true</StartWhenAvailable>
    <Enabled>false</Enabled>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT2M</ExecutionTimeLimit>
  </Settings>
  <Actions Context="Author">
    <Exec><Command>$launcherExe</Command><Arguments>$args</Arguments></Exec>
  </Actions>
</Task>
"@
}

function Set-QmEverythingWindowTask {
    [CmdletBinding()]
    param([string]$Root, [string]$DeployDir, [switch]$Restore)

    $scheduler = New-Object -ComObject 'Schedule.Service'
    $scheduler.Connect()
    try { $folder = $scheduler.GetFolder('\Everything NightIndex') }
    catch {
        if ($_.Exception.HResult -in -2147024894, -2147024893) { return 'Everything legacy task folder not found.' }
        throw
    }
    $tasks = @{}
    foreach ($task in $folder.GetTasks(1)) { $tasks[$task.Name] = $task }
    $stop = $tasks['Stop']
    $start = $tasks['Start']
    $gate = $tasks['QuietMode-Window']
    if (-not $stop -or -not $start) {
        if ($gate) { throw 'Everything window task exists but its legacy task pair is missing.' }
        return 'Everything legacy task pair not found.'
    }
    if ($Restore) {
        if (-not $gate) { return 'Everything window task was never installed.' }
        $previousGate = $gate.Enabled
        $previousStop = $stop.Enabled
        $previousStart = $start.Enabled
        $gate.Enabled = $false
        try {
            $stop.Enabled = $true
            $start.Enabled = $true
        } catch {
            $stop.Enabled = $previousStop
            $start.Enabled = $previousStart
            $gate.Enabled = $previousGate
            throw
        }
        return 'Restored the original Everything Stop and Start tasks.'
    }
    if (-not $gate -and (-not $stop.Enabled -or -not $start.Enabled)) {
        return 'Everything legacy tasks were disabled; their settings were preserved.'
    }

    $user = $stop.Definition.Principal.UserId
    $startUser = $start.Definition.Principal.UserId
    $stopAction = $stop.Definition.Actions.Item(1)
    $startAction = $start.Definition.Actions.Item(1)
    $exe = $stopAction.Path
    $launcher = Join-Path $DeployDir 'QuietMode.exe'
    if ($stop.Definition.Principal.LogonType -ne 3 -or $start.Definition.Principal.LogonType -ne 3 -or
        -not $user -or -not $startUser -or $exe -ne $startAction.Path -or
        [IO.Path]::GetFileName($exe) -ne 'Everything.exe' -or -not [IO.File]::Exists($exe) -or
        -not [IO.File]::Exists($launcher)) {
        throw 'Unexpected Everything legacy tasks or missing deployed QuietMode.exe launcher.'
    }
    $stopSid = $user
    if ($stopSid -notmatch '^S-1-') {
        $stopSid = [Security.Principal.NTAccount]::new($stopSid).Translate([Security.Principal.SecurityIdentifier]).Value
    }
    if ($startUser -notmatch '^S-1-') {
        $startUser = [Security.Principal.NTAccount]::new($startUser).Translate([Security.Principal.SecurityIdentifier]).Value
    }
    if ($stopSid -ne $startUser) { throw 'Everything legacy tasks use different accounts.' }

    $xml = Get-QmEverythingTaskXml -UserId $stopSid -Root $Root -Launcher $launcher -EverythingExe $exe -Today ([datetime]::Today)
    $oldStopEnabled = $stop.Enabled
    $oldStartEnabled = $start.Enabled
    $oldGateXml = if ($gate) { $gate.Xml }
    # Register disabled (6 = create/update, 3 = interactive token) before switching the pair off.
    $gate = $folder.RegisterTask('QuietMode-Window', $xml, 6, $stopSid, $null, 3, $null)
    try {
        $stop.Enabled = $false
        $start.Enabled = $false
        $gate.Enabled = $true
        if ($stop.Enabled -or $start.Enabled -or -not $gate.Enabled) {
            throw 'Everything task migration read-back failed.'
        }
    } catch {
        $gate.Enabled = $false
        $stop.Enabled = $oldStopEnabled
        $start.Enabled = $oldStartEnabled
        if ($oldGateXml) {
            [void]$folder.RegisterTask('QuietMode-Window', $oldGateXml, 6, $stopSid, $null, 3, $null)
        }
        throw
    }
    return 'Everything now follows QuietMode state in the interactive session.'
}

function Get-QmEverythingClients {
    # SYSTEM sees clients across user sessions. The Everything service in session 0 stays running.
    [Diagnostics.Process]::GetProcessesByName('Everything') | Where-Object { $_.SessionId -ne 0 }
}

function Confirm-QmEverythingForOffline {
    [CmdletBinding()]
    param([string]$Root, [string]$Cycle, [switch]$RequestExit, [int]$TimeoutSeconds = 35)

    $ErrorActionPreference = 'Stop'
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $requested = $false
    do {
        $state = [IO.File]::ReadAllText((Join-Path $Root 'state.json')) | ConvertFrom-Json
        if ($Cycle -notmatch '^[0-9a-f]{32}$' -or $state.everythingQuietCycle -cne $Cycle) {
            throw '静音状态已改变，取消本次脱机。'
        }
        $clients = @(Get-QmEverythingClients)
        if ($clients.Count -eq 0) { return }
        if (-not $RequestExit) { throw 'Everything 正在运行，保留磁盘在线。' }

        $since = [datetimeoffset]$state.everythingQuietSinceUtc
        foreach ($client in $clients) {
            if ($client.StartTime.ToUniversalTime() -gt $since.UtcDateTime) {
                throw '本次静音后重新打开了 Everything，保留进程及在线磁盘。'
            }
        }
        if (-not $requested) {
            # Run the installed WinExe in its registered interactive user session.
            Start-ScheduledTask -TaskPath '\Everything NightIndex\' -TaskName 'QuietMode-Window' -ErrorAction Stop
            $requested = $true
        }
        if ($clock.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
            throw '等待 Everything 退出超时，保留磁盘在线。'
        }
        Start-Sleep -Milliseconds 250
    } while ($true)
}

Export-ModuleMember -Function Get-QmEverythingTaskXml, Set-QmEverythingWindowTask, Confirm-QmEverythingForOffline
