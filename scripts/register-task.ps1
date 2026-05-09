if (!([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole] "Administrator")) { Start-Process powershell.exe "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`"" -Verb RunAs; exit }

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$action = New-ScheduledTaskAction `
    -Execute 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' `
    -Argument '-NoProfile -ExecutionPolicy Bypass -File C:\A.I.VOICE-API\scripts\runner.ps1' `
    -WorkingDirectory 'C:\A.I.VOICE-API'

$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Highest

$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew

Register-ScheduledTask -TaskName 'AIVoiceAPI' -Action $action -Principal $principal -Settings $settings -Force
Write-Host '[AIVoiceAPI] Task registered OK'
Write-Host '[AIVoiceAPI] 起動するには start.bat を実行してください'
