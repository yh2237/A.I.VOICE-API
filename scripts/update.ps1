[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$API_DIR = 'C:\A.I.VOICE-API'

Write-Host '=== A.I.VOICE-API Update ==='

Write-Host '[1/4] Stopping service...'
& "$API_DIR\scripts\stop.ps1"
Start-Sleep -Seconds 2

Write-Host '[2/4] Pulling latest code...'
$prevDir = Get-Location
Set-Location $API_DIR
$isGit = Test-Path '.git'
if ($isGit) {
    git pull 2>&1 | ForEach-Object { Write-Host "  $_" }
} else {
    Write-Host '  (not a git repo, skipping git pull)'
}

Write-Host '[3/4] Installing dependencies...'
npm install --omit=dev 2>&1 | ForEach-Object { Write-Host "  $_" }

Write-Host '[4/4] Starting service...'
& "$API_DIR\scripts\start.ps1"

Set-Location $prevDir
Write-Host ''
Write-Host 'Update complete. Run status.bat to verify.'
