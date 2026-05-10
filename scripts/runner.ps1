$ErrorActionPreference = 'Continue'
$API_DIR  = 'C:\A.I.VOICE-API'
$SERVER_JS = 'C:\A.I.VOICE-API\server.js'
$LOG_DIR  = 'C:\A.I.VOICE-API\logs'
$PID_FILE = 'C:\A.I.VOICE-API\api.pid'

if (-not (Test-Path $LOG_DIR)) { New-Item -ItemType Directory $LOG_DIR | Out-Null }
$LOG_FILE = Join-Path $LOG_DIR ("api-" + (Get-Date -Format 'yyyyMMdd') + ".log")

function Write-Log([string]$msg) {
    $line = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + " " + $msg
    Add-Content -Path $LOG_FILE -Value $line -Encoding UTF8
}

function Resolve-NodeExe {
    $configFile = Join-Path $API_DIR 'config\config.yml'
    if (Test-Path $configFile) {
        try {
            $raw = Get-Content $configFile -Raw -Encoding UTF8
            if ($raw -match 'node_exe:\s*"(.+?)"') {
                $candidate = $matches[1]
                if ($candidate -and (Test-Path $candidate)) {
                    return $candidate
                }
            }
        } catch {}
    }

    $fromCmd = (Get-Command node -ErrorAction SilentlyContinue).Source
    if ($fromCmd -and (Test-Path $fromCmd)) {
        return $fromCmd
    }

    $common = @(
        "$env:ProgramFiles\nodejs\node.exe",
        "${env:ProgramFiles(x86)}\nodejs\node.exe",
        "$env:LOCALAPPDATA\Programs\nodejs\node.exe",
        "$env:APPDATA\nvm\node.exe"
    )
    foreach ($p in $common) {
        if (Test-Path $p) { return $p }
    }

    return $null
}

$NODE_EXE = Resolve-NodeExe
if (-not $NODE_EXE) {
    Write-Log "[RUNNER] FATAL: Cannot find node.exe. Set aivoice.node_exe in config/config.yml"
    exit 1
}
Write-Log "[RUNNER] node.exe = $NODE_EXE"

if (Test-Path $PID_FILE) {
    $existingPid = (Get-Content $PID_FILE -ErrorAction SilentlyContinue).Trim()
    if ($existingPid) {
        $existing = Get-Process -Id ([int]$existingPid) -ErrorAction SilentlyContinue
        if ($existing) {
            Write-Log "[RUNNER] Already running (PID=$existingPid), exiting."
            exit 0
        }
    }
}

$myPid = [System.Diagnostics.Process]::GetCurrentProcess().Id
$mySession = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
[string]$myPid | Out-File $PID_FILE -Encoding ascii -NoNewline
Write-Log "[RUNNER] aivoice-api-runner started (PID=$myPid, SessionId=$mySession)"

$maxCrashes   = 10
$crashWindow  = 60
$restartDelay = 3
$crashTimes   = [System.Collections.Generic.List[datetime]]::new()

try {
    while ($true) {
        Write-Log "[RUNNER] Starting A.I.VOICE-API..."

        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $NODE_EXE
        $psi.Arguments = $SERVER_JS
        $psi.WorkingDirectory = $API_DIR
        $psi.UseShellExecute = $true
        $psi.CreateNoWindow = $true

        $proc = [System.Diagnostics.Process]::Start($psi)

        if (-not $proc) {
            Write-Log "[RUNNER] FATAL: Process.Start() returned null"
            break
        }

        [string]$proc.Id | Out-File $PID_FILE -Encoding ascii -NoNewline
        Write-Log "[RUNNER] A.I.VOICE-API started (PID=$($proc.Id))"

        $proc.WaitForExit()
        $exitCode = $proc.ExitCode
        Write-Log "[RUNNER] A.I.VOICE-API exited (code=$exitCode)"

        if ($exitCode -eq 0) {
            Write-Log "[RUNNER] Clean exit, stopping."
            break
        }

        $now = Get-Date
        $crashTimes.Add($now)
        $cutoff = $now.AddSeconds(-$crashWindow)
        $crashTimes.RemoveAll([Predicate[datetime]]{ param($t) $t -lt $cutoff }) | Out-Null

        if ($crashTimes.Count -ge $maxCrashes) {
            Write-Log "[RUNNER] Too many crashes ($($crashTimes.Count) in ${crashWindow}s), giving up."
            break
        }

        Write-Log "[RUNNER] Restarting in ${restartDelay}s..."
        Start-Sleep -Seconds $restartDelay
    }
} finally {
    Remove-Item $PID_FILE -Force -ErrorAction SilentlyContinue
    Write-Log "[RUNNER] aivoice-api-runner finished."
}
