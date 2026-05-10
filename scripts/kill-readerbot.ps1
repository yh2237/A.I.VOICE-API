[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Write-Host '[AIVoiceAPI] Terminating ReaderBot processes...'

Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -like '*ReaderBot*' } |
    ForEach-Object {
        Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
        Write-Host "  Killed powershell PID=$($_.ProcessId)"
    }

Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -like '*ReaderBot*index.js*' } |
    ForEach-Object {
        Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
        Write-Host "  Killed node PID=$($_.ProcessId)"
    }

Remove-Item C:\ReaderBot\bot.pid -Force -ErrorAction SilentlyContinue

Write-Host '[AIVoiceAPI] Done.'
