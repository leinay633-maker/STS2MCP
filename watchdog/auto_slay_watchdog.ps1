param(
    [string]$BaseUrl = "http://localhost:15526/api/v1/singleplayer",
    [string]$SteamExe = "D:\steam\steam.exe",
    [string]$SteamLaunchArgs = "-applaunch 2868840",
    [string]$GameExe = "D:\steam\steamapps\common\Slay the Spire 2\SlayTheSpire2.exe",
    [string]$GameProcessName = "SlayTheSpire2",
    [int]$PollSeconds = 3,
    [int]$StallThreshold = 10,
    [string]$LogPath = "D:\steam\steamapps\common\Slay the Spire 2\mods\STS2_MCP.learning\watchdog.log",
    [string]$AlertDir = "D:\steam\steamapps\common\Slay the Spire 2\mods\STS2_MCP.learning\alerts",
    [int]$AlertCooldownSeconds = 300,
    [int]$RequestTimeoutSeconds = 8,
    [int]$HeartbeatSeconds = 60
)

$ErrorActionPreference = "Stop"

function Write-WatchdogLog {
    param([string]$Message)

    $dir = Split-Path -Parent $LogPath
    if ($dir) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }

    $line = "[{0}] {1}" -f ([DateTime]::Now.ToString("yyyy-MM-dd HH:mm:ss")), $Message
    Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8
}

function Get-RecentWatchdogLog {
    if (Test-Path $LogPath) {
        return @(Get-Content -LiteralPath $LogPath -Tail 40)
    }

    return @()
}

function Get-RecentRunLogTail {
    param($State)

    $path = [string]$State.auto_slay.current_log_file
    if ($path -and (Test-Path $path)) {
        return @(Get-Content -LiteralPath $path -Tail 60)
    }

    return @()
}

function Show-AlertPopup {
    param([string]$Message)

    try {
        $shell = New-Object -ComObject WScript.Shell
        $null = $shell.Popup($Message, 20, "STS2 MCP Watchdog", 0x30)
    }
    catch {
        Write-WatchdogLog ("Popup failed: {0}" -f $_.Exception.Message)
    }
}

function Resolve-GameExe {
    param([string]$PreferredGameExe)

    $candidates = [System.Collections.Generic.List[string]]::new()
    foreach ($candidate in @(
        $PreferredGameExe,
        "D:\steam\steamapps\common\Slay the Spire 2\SlayTheSpire2.exe",
        "D:\SteamLibrary\steamapps\common\Slay the Spire 2\SlayTheSpire2.exe",
        "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\SlayTheSpire2.exe",
        "C:\Program Files\Steam\steamapps\common\Slay the Spire 2\SlayTheSpire2.exe"
    )) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and -not $candidates.Contains($candidate)) {
            $candidates.Add($candidate)
        }
    }

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return (Resolve-Path $candidate).Path
        }
    }

    return $null
}

function Start-ResolvedGameProcess {
    $resolvedGameExe = Resolve-GameExe -PreferredGameExe $GameExe
    if ($resolvedGameExe) {
        if ($resolvedGameExe -ne $script:ResolvedGameExe) {
            Write-WatchdogLog ("Resolved game executable: {0}" -f $resolvedGameExe)
            $script:ResolvedGameExe = $resolvedGameExe
        }

        Write-WatchdogLog "Game process missing. Launching executable directly."
        Start-Process -FilePath $resolvedGameExe -WorkingDirectory (Split-Path -Parent $resolvedGameExe) | Out-Null
        return
    }

    Write-WatchdogLog "Game executable not found. Falling back to Steam."
    Start-Process -FilePath $SteamExe -ArgumentList $SteamLaunchArgs | Out-Null
}

function Raise-Alert {
    param(
        [string]$Reason,
        $State
    )

    $now = Get-Date
    if ($script:lastAlertAt -and (($now - $script:lastAlertAt).TotalSeconds -lt $AlertCooldownSeconds)) {
        Write-WatchdogLog ("Alert suppressed by cooldown. reason={0}" -f $Reason)
        return
    }

    $script:lastAlertAt = $now
    New-Item -ItemType Directory -Force -Path $AlertDir | Out-Null

    $stamp = $now.ToString("yyyyMMdd-HHmmss")
    $snapshotPath = Join-Path $AlertDir ("alert-{0}.json" -f $stamp)
    $wakePath = Join-Path $AlertDir "WAKE_CODEX.txt"
    $latestPath = Join-Path $AlertDir "latest-alert.json"

    $payload = [ordered]@{
        timestamp = $now.ToString("o")
        reason = $Reason
        state = $State
        watchdog_log_tail = Get-RecentWatchdogLog
        run_log_tail = Get-RecentRunLogTail $State
    }

    $json = $payload | ConvertTo-Json -Depth 20
    Set-Content -LiteralPath $snapshotPath -Value $json -Encoding UTF8
    Set-Content -LiteralPath $latestPath -Value $json -Encoding UTF8
    Set-Content -LiteralPath $wakePath -Value @(
        "STS2 MCP watchdog detected a bug and needs attention."
        ("time: {0}" -f $now.ToString("yyyy-MM-dd HH:mm:ss"))
        ("reason: {0}" -f $Reason)
        ("snapshot: {0}" -f $snapshotPath)
        "Open this snapshot and hand it to Codex."
    ) -Encoding UTF8

    Write-WatchdogLog ("ALERT: {0} snapshot={1}" -f $Reason, $snapshotPath)
    Show-AlertPopup ("STS2 MCP watchdog detected a bug.`nreason: {0}`nsnapshot: {1}" -f $Reason, $snapshotPath)
}

function Get-JsonState {
    try {
        return Invoke-RestMethod -Uri $BaseUrl -Method Get -TimeoutSec $RequestTimeoutSeconds
    }
    catch {
        return $null
    }
}

function Post-Action {
    param([string]$Action)

    try {
        $body = @{ action = $Action } | ConvertTo-Json -Compress
        return Invoke-RestMethod -Uri $BaseUrl -Method Post -ContentType "application/json" -Body $body -TimeoutSec $RequestTimeoutSeconds
    }
    catch {
        Write-WatchdogLog ("POST {0} failed: {1}" -f $Action, $_.Exception.Message)
        return $null
    }
}

function Ensure-GameRunning {
    $proc = Get-Process -Name $GameProcessName -ErrorAction SilentlyContinue
    if ($proc) {
        return $true
    }

    Start-ResolvedGameProcess
    return $false
}

function Get-StateFingerprint {
    param($State)

    if (-not $State) {
        return "no-state"
    }

    $parts = [System.Collections.Generic.List[string]]::new()
    $parts.Add([string]$State.state_type)
    $parts.Add([string]$State.auto_slay.iteration)
    $parts.Add([string]$State.auto_slay.current_seed)
    $parts.Add([string]$State.auto_slay.current_log_file)

    if ($State.run) {
        $parts.Add(("act:{0}" -f $State.run.act))
        $parts.Add(("floor:{0}" -f $State.run.floor))
    }

    if ($State.player) {
        $parts.Add(("hp:{0}" -f $State.player.hp))
        $parts.Add(("gold:{0}" -f $State.player.gold))
        $parts.Add(("hand:{0}" -f @($State.player.hand).Count))
    }

    if ($State.battle) {
        $parts.Add(("round:{0}" -f $State.battle.round))
        $parts.Add(("turn:{0}" -f $State.battle.turn))
        foreach ($enemy in @($State.battle.enemies)) {
            $parts.Add(("{0}:{1}" -f $enemy.entity_id, $enemy.hp))
        }
    }

    return ($parts -join "|")
}

function Restart-Game {
    Write-WatchdogLog "Restarting game process."
    Get-Process -Name $GameProcessName -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 3
    Start-ResolvedGameProcess
}

Write-WatchdogLog "Watchdog started. poll=${PollSeconds}s stall_threshold=${StallThreshold}"

$lastFingerprint = ""
$sameFingerprintCount = 0
$httpFailures = 0
$overlayRecoveryCount = 0
$script:lastAlertAt = $null
$lastHeartbeatAt = Get-Date
$script:ResolvedGameExe = Resolve-GameExe -PreferredGameExe $GameExe
if ($script:ResolvedGameExe) {
    Write-WatchdogLog ("Initial game executable: {0}" -f $script:ResolvedGameExe)
}
else {
    Write-WatchdogLog "Initial game executable not found. Steam fallback is armed."
}

while ($true) {
    try {
        if (((Get-Date) - $lastHeartbeatAt).TotalSeconds -ge $HeartbeatSeconds) {
            Write-WatchdogLog "heartbeat"
            $lastHeartbeatAt = Get-Date
        }

        $gameWasRunning = Ensure-GameRunning
        if (-not $gameWasRunning) {
            $httpFailures = 0
            $sameFingerprintCount = 0
            Start-Sleep -Seconds 20
            continue
        }

        $state = Get-JsonState
        if (-not $state) {
            $httpFailures++
            Write-WatchdogLog ("HTTP unavailable. failures={0}" -f $httpFailures)
            if ($httpFailures -ge 8) {
                Raise-Alert -Reason ("HTTP unavailable for {0} consecutive checks" -f $httpFailures) -State $null
                Restart-Game
                $httpFailures = 0
                $sameFingerprintCount = 0
                Start-Sleep -Seconds 20
            }
            else {
                Start-Sleep -Seconds $PollSeconds
            }
            continue
        }

        $httpFailures = 0

        $fingerprint = Get-StateFingerprint $state
        if ($fingerprint -eq $lastFingerprint) {
            $sameFingerprintCount++
        }
        else {
            $lastFingerprint = $fingerprint
            $sameFingerprintCount = 0
        }

        if (-not $state.auto_slay.is_active) {
            if ($state.state_type -eq "overlay") {
                $overlayRecoveryCount++
                Write-WatchdogLog ("auto_slay inactive on overlay. attempts={0}" -f $overlayRecoveryCount)
                if ($overlayRecoveryCount -ge 3 -or ([string]$state.auto_slay.last_error) -match "Game over screen main menu button is not available") {
                    Raise-Alert -Reason "auto_slay stuck on overlay" -State $state
                    Restart-Game
                    $overlayRecoveryCount = 0
                    $sameFingerprintCount = 0
                    $httpFailures = 0
                    Start-Sleep -Seconds 20
                    continue
                }
            }
            else {
                $overlayRecoveryCount = 0
            }

            Write-WatchdogLog ("auto_slay inactive at state_type={0}. Starting loop." -f $state.state_type)
            Post-Action "auto_slay_start_loop" | Out-Null
            Start-Sleep -Seconds $PollSeconds
            continue
        }

        $overlayRecoveryCount = 0

        if ($sameFingerprintCount -ge $StallThreshold) {
            Raise-Alert -Reason ("state stalled for {0} checks" -f $sameFingerprintCount) -State $state
            Write-WatchdogLog ("State stalled for {0} checks. Restarting loop." -f $sameFingerprintCount)
            Post-Action "auto_slay_stop" | Out-Null
            Start-Sleep -Seconds 2
            Post-Action "auto_slay_start_loop" | Out-Null
            $sameFingerprintCount = 0
            Start-Sleep -Seconds $PollSeconds
            continue
        }
    }
    catch {
        Write-WatchdogLog ("Watchdog loop error: {0}" -f $_.Exception)
    }

    Start-Sleep -Seconds $PollSeconds
}
