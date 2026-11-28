#Requires -Version 5.1

BeforeAll {
    $repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
    Import-Module (Join-Path $repo 'powershell\Modules\QuietMode.Everything.psm1') -Force

    # 被测实现是 QuietMode.exe 的 --everything-window。它是 WinExe（GUI 子系统）：
    # 从计划任务启动不创建控制台，这是"不闪窗"的根。副作用是 Console.Out 没有接收方，
    # 所以预览结果走 -PreviewFile，并且必须用 Start-Process -Wait 拿退出码
    # （PowerShell 用 & 调 GUI 程序不会等待，$LASTEXITCODE 不可信）。
    $script:exe = Join-Path $repo 'QuietMode\build\bin\Release\QuietMode.exe'

    function Set-EverythingTestState([hashtable]$State) {
        $json = $State | ConvertTo-Json
        [IO.File]::WriteAllText((Join-Path $TestDrive 'state.json'), $json, [Text.UTF8Encoding]::new($false))
    }

    function Get-EverythingTestPhase {
        $out = Join-Path $TestDrive ('preview-' + [guid]::NewGuid().ToString('N') + '.txt')
        $p = Start-Process -FilePath $script:exe -ArgumentList @(
            '--everything-window', '-Root', $TestDrive,
            '-EverythingExe', 'C:\Unused\Everything.exe', '-Preview', '-PreviewFile', $out
        ) -Wait -PassThru -WindowStyle Hidden
        return [pscustomobject]@{ Code = $p.ExitCode; Key = (Get-Content $out -Raw -ErrorAction SilentlyContinue) }
    }
}

Describe 'Everything follows the current QuietMode state' {
    It 'exits once per quiet activation, including while the night worker is pending' {
        $cycle = '0123456789abcdef0123456789abcdef'
        $since = '2026-11-26T16:30:00.0000000Z'
        Set-EverythingTestState @{ everythingQuietCycle = $cycle; everythingQuietSinceUtc = $since; autoSilentActive = $false }
        (Get-EverythingTestPhase).Key | Should -Be "quiet/$cycle"
        Set-EverythingTestState @{ everythingQuietCycle = $cycle; everythingQuietSinceUtc = $since; autoSilentActive = $true }
        (Get-EverythingTestPhase).Key | Should -Be "quiet/$cycle"
    }

    It 'restores on manual off even if day restoration has not finished' {
        Set-EverythingTestState @{
            everythingQuietCycle = ''
            autoSilentActive = $true
            autoSilentSuppressed = $true
            pendingMorningRestore = $true
        }
        (Get-EverythingTestPhase).Key | Should -Be 'normal'
    }

    It 'reads the legacy state until the first upgraded reconcile' {
        Set-EverythingTestState @{ modeOn = $true; autoSilentActive = $false }
        (Get-EverythingTestPhase).Key | Should -Be 'quiet/legacy'
        Set-EverythingTestState @{ modeOn = $false; autoSilentActive = $true; autoSilentSuppressed = $true }
        (Get-EverythingTestPhase).Key | Should -Be 'normal'
    }

    It 'rejects invalid state instead of changing Everything' {
        Set-EverythingTestState @{ everythingQuietCycle = 123 }
        $r = Get-EverythingTestPhase
        $r.Code | Should -Be 3
        $r.Key | Should -BeNullOrEmpty

        Set-EverythingTestState @{}
        $r2 = Get-EverythingTestPhase
        $r2.Code | Should -Be 3
    }

    It 'registers periodic execution without an offset or a wake timer' {
        $parameters = @{
            UserId = 'S-1-5-21-1-2-3-1001'
            Root = 'C:\Quiet Mode & Data'
            Launcher = 'H:\Toolkit\QuietMode.exe'
            EverythingExe = 'C:\Program Files\Everything\Everything.exe'
            Today = '2026-11-27'
        }
        [xml]$xml = Get-QmEverythingTaskXml @parameters
        $xml.Task.Triggers.TimeTrigger.StartBoundary | Should -Be '2026-11-26T00:00:00'
        $xml.Task.Triggers.TimeTrigger.Repetition.Interval | Should -Be 'PT1M'
        $xml.Task.Principals.Principal.LogonType | Should -Be 'InteractiveToken'
        $xml.Task.Principals.Principal.RunLevel | Should -Be 'LeastPrivilege'
        $xml.Task.Settings.WakeToRun | Should -Be 'false'
        $xml.Task.Actions.Exec.Arguments | Should -Match '-Root "C:\\Quiet Mode & Data"'
    }

    It 'launches a GUI-subsystem host so no console window is created' {
        $parameters = @{
            UserId = 'S-1-5-21-1-2-3-1001'
            Root = 'C:\ProgramData\QuietMode'
            Launcher = 'H:\Toolkit\QuietMode.exe'
            EverythingExe = 'C:\Program Files\Everything\Everything.exe'
            Today = '2026-11-27'
        }
        [xml]$xml = Get-QmEverythingTaskXml @parameters
        # 关键不变量：动作必须是 QuietMode.exe（WinExe），绝不能再回到 powershell.exe；
        # 也不能带 -WindowStyle Hidden 这种"先建窗口再隐藏"的假隐藏。
        $xml.Task.Actions.Exec.Command | Should -Be 'H:\Toolkit\QuietMode.exe'
        $xml.Task.Actions.Exec.Command | Should -Not -Match 'powershell'
        $xml.Task.Actions.Exec.Arguments | Should -Match '--everything-window'
        $xml.Task.Actions.Exec.Arguments | Should -Not -Match 'WindowStyle'
    }
}

Describe 'Everything before disk offline' {
    BeforeEach {
        $script:cycle = '0123456789abcdef0123456789abcdef'
        Set-EverythingTestState @{ everythingQuietCycle=$script:cycle; everythingQuietSinceUtc='2026-11-27T12:00:00Z' }
        Mock Start-ScheduledTask -ModuleName QuietMode.Everything {}
    }

    It 'requests exit in the interactive session and waits for the client to disappear' {
        InModuleScope QuietMode.Everything -Parameters @{ rootPath=$TestDrive; cycle=$script:cycle } {
            param($rootPath,$cycle)
            $script:clientReads = 0
            Mock Get-QmEverythingClients {
                $script:clientReads++
                if ($script:clientReads -eq 1) { [pscustomobject]@{ StartTime=[datetime]'2026-11-26' } }
            }
            Confirm-QmEverythingForOffline -Root $rootPath -Cycle $cycle -RequestExit
            Should -Invoke Start-ScheduledTask -Times 1 -Exactly -ParameterFilter {
                $TaskName -eq 'QuietMode-Window' -and $TaskPath -eq '\Everything NightIndex\'
            }
            $script:clientReads | Should -Be 2
        }
    }

    It 'allows offline without a registered task when no client is running' {
        Mock Get-QmEverythingClients -ModuleName QuietMode.Everything {}
        Confirm-QmEverythingForOffline -Root $TestDrive -Cycle $script:cycle -RequestExit
        Should -Invoke Start-ScheduledTask -ModuleName QuietMode.Everything -Times 0 -Exactly
    }

    It 'blocks offline on timeout and on a task launch error' {
        Mock Get-QmEverythingClients -ModuleName QuietMode.Everything { [pscustomobject]@{ StartTime=[datetime]'2026-11-26' } }
        { Confirm-QmEverythingForOffline -Root $TestDrive -Cycle $script:cycle -RequestExit -TimeoutSeconds 0 } | Should -Throw '*超时*'
        Mock Start-ScheduledTask -ModuleName QuietMode.Everything { throw 'task unavailable' }
        { Confirm-QmEverythingForOffline -Root $TestDrive -Cycle $script:cycle -RequestExit } | Should -Throw '*task unavailable*'
    }

    It 'preserves a manual relaunch both before waiting and immediately before offline' {
        Mock Get-QmEverythingClients -ModuleName QuietMode.Everything { [pscustomobject]@{ StartTime=[datetime]'2026-11-29' } }
        { Confirm-QmEverythingForOffline -Root $TestDrive -Cycle $script:cycle -RequestExit } | Should -Throw '*重新打开*'
        { Confirm-QmEverythingForOffline -Root $TestDrive -Cycle $script:cycle } | Should -Throw '*正在运行*'
        Should -Invoke Start-ScheduledTask -ModuleName QuietMode.Everything -Times 0 -Exactly
    }

    It 'rejects stale requests after manual quiet is turned off' {
        Set-EverythingTestState @{ everythingQuietCycle='' }
        { Confirm-QmEverythingForOffline -Root $TestDrive -Cycle $script:cycle } | Should -Throw '*已改变*'
        Should -Invoke Start-ScheduledTask -ModuleName QuietMode.Everything -Times 0 -Exactly
    }
}
