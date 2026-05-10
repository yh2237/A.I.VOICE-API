[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$whoami = whoami 2>$null
if (-not $whoami) {
    $whoami = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
}
Write-Host "[AIVoiceAPI] Registering task for: $whoami"

$action = New-ScheduledTaskAction `
    -Execute 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' `
    -Argument '-NoProfile -ExecutionPolicy Bypass -File C:\A.I.VOICE-API\scripts\runner.ps1' `
    -WorkingDirectory 'C:\A.I.VOICE-API'

$trigger = New-ScheduledTaskTrigger -AtLogon

$principal = New-ScheduledTaskPrincipal -UserId $whoami -LogonType Interactive -RunLevel Highest

$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew

try {
    Register-ScheduledTask -TaskName 'AIVoiceAPI' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force -ErrorAction Stop
    Write-Host '[AIVoiceAPI] Task registered OK (auto-start at logon)'
    Write-Host '[AIVoiceAPI] Manage with: start.bat / stop.bat / status.bat / update.bat'
} catch {
    Write-Host "[AIVoiceAPI] Register failed: $_"
    Write-Host "[AIVoiceAPI] Try running this from an admin PowerShell terminal, not SSH."
}
