[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$pidFile = 'C:\A.I.VOICE-API\api.pid'

try {
    $task = Get-ScheduledTask -TaskName 'AIVoiceAPI' -ErrorAction SilentlyContinue
    if ($task) {
        Stop-ScheduledTask -TaskName 'AIVoiceAPI' -ErrorAction SilentlyContinue
        Write-Host '[AIVoiceAPI] Scheduled Task stopped.'
    }
} catch {
    Write-Host "[AIVoiceAPI] Scheduled Task stop error: $($_.Exception.Message)"
}

$runnerProcs = Get-CimInstance Win32_Process -Filter "Name='powershell.exe' OR Name='pwsh.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -like '*runner.ps1*' }

foreach ($rp in $runnerProcs) {
    try {
        Stop-Process -Id $rp.ProcessId -Force -ErrorAction SilentlyContinue
        Write-Host "[AIVoiceAPI] Runner stopped (PID=$($rp.ProcessId))"
    } catch { }
}

Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -like '*A.I.VOICE-API*server.js*' } |
    ForEach-Object {
        try {
            Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
            Write-Host "[AIVoiceAPI] Node stopped (PID=$($_.ProcessId))"
        } catch { }
    }

if (Test-Path $pidFile) {
    $botPid = (Get-Content $pidFile -ErrorAction SilentlyContinue).Trim()
    if ($botPid) {
        $p = Get-Process -Id ([int]$botPid) -ErrorAction SilentlyContinue
        if ($p) {
            Stop-Process -Id ([int]$botPid) -Force
            Write-Host "[AIVoiceAPI] PID $botPid stopped."
        }
    }
    Remove-Item $pidFile -Force -ErrorAction SilentlyContinue
} else {
    Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like '*A.I.VOICE-API*server.js*' } |
        ForEach-Object {
            Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
            Write-Host "[AIVoiceAPI] PID $($_.ProcessId) stopped."
        }
}

Write-Host '[AIVoiceAPI] Stopped.'
