[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$pidFile = 'C:\A.I.VOICE-API\api.pid'
$running = $false
$botPid = $null

if (Test-Path $pidFile) {
    $botPid = (Get-Content $pidFile -ErrorAction SilentlyContinue).Trim()
    if ($botPid) {
        $p = Get-Process -Id ([int]$botPid) -ErrorAction SilentlyContinue
        if ($p) { $running = $true }
    }
}

Write-Host '=== A.I.VOICE-API Status ==='
if ($running) {
    $p = Get-Process -Id ([int]$botPid)
    Write-Host 'Status  : ' -NoNewline
    Write-Host 'Running' -ForegroundColor Green
    Write-Host "PID     : $botPid"
    Write-Host "Session : $($p.SessionId)"
    Write-Host "Started : $($p.StartTime)"
    Write-Host "CPU     : $([math]::Round($p.CPU, 2))s"
    Write-Host "Memory  : $([math]::Round($p.WorkingSet64 / 1MB, 1)) MB"
} else {
    Write-Host 'Status  : ' -NoNewline
    Write-Host 'Stopped' -ForegroundColor Red
}

Write-Host ''
Write-Host '=== AIVoiceEditor ==='
$av = Get-Process -Name AIVoiceEditor -ErrorAction SilentlyContinue
if ($av) {
    Write-Host 'Status  : ' -NoNewline
    Write-Host 'Running' -ForegroundColor Green
    Write-Host "PID     : $($av.Id)"
    Write-Host "Session : $($av.SessionId)"
} else {
    Write-Host 'Status  : ' -NoNewline
    Write-Host 'Not running' -ForegroundColor Red
}

Write-Host ''
Write-Host '=== Latest Log ==='
$log = Get-ChildItem 'C:\A.I.VOICE-API\logs\api-*.log' -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -notlike 'api-out-*' } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if ($log) {
    Write-Host "File: $($log.Name)"
    Get-Content $log.FullName -Encoding UTF8 -Tail 10
} else {
    Write-Host '(no logs)'
}

Write-Host ''
Write-Host '=== API Health ==='
try {
    $res = Invoke-RestMethod -Uri 'http://localhost:58080/health' -TimeoutSec 3 -ErrorAction Stop
    Write-Host 'API     : ' -NoNewline
    Write-Host 'Healthy' -ForegroundColor Green
    $status = Invoke-RestMethod -Uri 'http://localhost:58080/api/status' -TimeoutSec 3 -ErrorAction Stop
    Write-Host 'A.I.VOICE: ' -NoNewline
    if ($status.connected) {
        Write-Host $status.hostName -ForegroundColor Green
        Write-Host "Presets : $($status.presetNames -join ', ')"
    } else {
        Write-Host 'Disconnected' -ForegroundColor Yellow
    }
} catch {
    Write-Host 'API     : ' -NoNewline
    Write-Host 'Unreachable' -ForegroundColor Red
}
