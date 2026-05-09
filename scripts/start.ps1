[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$pidFile = 'C:\A.I.VOICE-API\api.pid'

if (Test-Path $pidFile) {
    $botPid = (Get-Content $pidFile -ErrorAction SilentlyContinue).Trim()
    if ($botPid) {
        $p = Get-Process -Id ([int]$botPid) -ErrorAction SilentlyContinue
        if ($p) {
            Write-Host '[AIVoiceAPI] Already running (PID=' -NoNewline
            Write-Host $botPid -ForegroundColor Green -NoNewline
            Write-Host ')'
            Write-Host '[AIVoiceAPI] Stop with stop.bat first.'
            exit 1
        }
    }
}

$task = Get-ScheduledTask -TaskName 'AIVoiceAPI' -ErrorAction SilentlyContinue
if (-not $task) {
    Write-Host '[AIVoiceAPI] Scheduled Task not found.'
    Write-Host '[AIVoiceAPI] Run scripts\register-task.ps1 first.'
    exit 1
}

if ($task.Settings.Enabled) {
    try {
        Start-ScheduledTask -TaskName 'AIVoiceAPI'
        Write-Host '[AIVoiceAPI] Start command sent. Check status with status.bat'
        exit 0
    } catch {
        Write-Host "[AIVoiceAPI] Failed to start: $($_.Exception.Message)"
        exit 1
    }
}

Write-Host '[AIVoiceAPI] Scheduled Task is disabled.'
Write-Host '[AIVoiceAPI] Enable it in Task Scheduler and retry.'
exit 1
