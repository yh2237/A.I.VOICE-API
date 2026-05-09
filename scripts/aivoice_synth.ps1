[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::InputEncoding  = [System.Text.Encoding]::UTF8

$ErrorActionPreference = 'Stop'

function Send-Response($obj) {
    $json = $obj | ConvertTo-Json -Compress -Depth 5
    [Console]::WriteLine($json)
    [Console]::Out.Flush()
}

function Send-Ok($data) {
    if ($null -eq $data) {
        Send-Response @{ ok = $true }
    } else {
        Send-Response @{ ok = $true; data = $data }
    }
}

function Send-Err([string]$message) {
    Send-Response @{ ok = $false; error = $message }
}

function Wait-HostStart($tts) {
    for ($i = 0; $i -lt 30; $i++) {
        if ($tts.Status.ToString() -ne 'NotRunning') { return }
        Start-Sleep -Milliseconds 200
    }
}

$script:ttsVersion = $null
$script:lastMasterControl = $null
$script:lastPresetName = $null

function Invoke-Init($msg) {
    $dllPath = $msg.dllPath
    if (-not [System.IO.File]::Exists($dllPath)) {
        Send-Err "DLL not found: $dllPath"
        return $null
    }

    Add-Type -Path $dllPath
    $tts = New-Object AI.Talk.Editor.Api.TtsControl
    $hosts = $tts.GetAvailableHostNames()

    if ($null -eq $hosts -or $hosts.Length -eq 0) {
        Send-Err 'No available hosts. Make sure A.I.VOICE Editor is running.'
        return $null
    }

    $targetHost = $msg.hostName
    if ([string]::IsNullOrEmpty($targetHost)) {
        $targetHost = $hosts[0]
    }

    $tts.Initialize($targetHost)

    if ($tts.Status.ToString() -eq 'NotRunning') {
        $tts.StartHost()
        Wait-HostStart $tts
    }

    $tts.Connect()

    $script:ttsVersion = $tts.Version
    $script:lastMasterControl = $null
    $presets = @($tts.VoicePresetNames)
    $result = @{ hostName = $targetHost; version = $script:ttsVersion; presetNames = $presets }
    Send-Ok $result
    return $tts
}

function Invoke-Synth($tts, $msg) {
    if ($null -eq $tts) {
        Send-Err 'Not initialized'
        return
    }

    if ($msg.preset -ne $script:lastPresetName) {
        $tts.CurrentVoicePresetName = $msg.preset
        $script:lastPresetName = $msg.preset
    }

    $volume       = if ($null -ne $msg.volume)       { [double]$msg.volume }       else { 1.0 }
    $speed        = if ($null -ne $msg.speed)        { [double]$msg.speed }        else { 1.0 }
    $pitch        = if ($null -ne $msg.pitch)        { [double]$msg.pitch }        else { 1.0 }
    $pitchRange   = if ($null -ne $msg.pitchRange)   { [double]$msg.pitchRange }   else { 1.0 }
    $middlePause  = if ($null -ne $msg.middlePause)  { [int]$msg.middlePause }     else { 150 }
    $longPause    = if ($null -ne $msg.longPause)    { [int]$msg.longPause }       else { 370 }
    $sentencePause = if ($null -ne $msg.sentencePause) { [int]$msg.sentencePause } else { 800 }

    $mc = @{
        Volume        = $volume
        Speed         = $speed
        Pitch         = $pitch
        PitchRange    = $pitchRange
        MiddlePause   = $middlePause
        LongPause     = $longPause
        SentencePause = $sentencePause
    }
    $mcJson = $mc | ConvertTo-Json -Compress
    if ($mcJson -ne $script:lastMasterControl) {
        $tts.MasterControl = $mcJson
        $script:lastMasterControl = $mcJson
    }
    $tts.Text = $msg.text
    $tts.SaveAudioToFile($msg.outputPath)

    Send-Ok @{ outputPath = $msg.outputPath }
}

$tts = $null

while ($true) {
    $line = $null
    try {
        $line = [Console]::ReadLine()
    } catch {
        break
    }
    if ($null -eq $line) { break }
    $line = $line.Trim()
    if ($line -eq '') { continue }

    $msg = $null
    try {
        $msg = $line | ConvertFrom-Json
    } catch {
        Send-Err "JSON parse error: $_"
        continue
    }

    if ($msg.type -eq 'init') {
        try {
            $tts = Invoke-Init $msg
        } catch {
            $tts = $null
            Send-Err "init failed: $_"
        }

    } elseif ($msg.type -eq 'synth') {
        try {
            Invoke-Synth $tts $msg
        } catch {
            Send-Err "synth failed: $_"
        }

    } elseif ($msg.type -eq 'keepalive') {
        if ($null -eq $tts) {
            Send-Err 'Not initialized'
        } else {
            try {
                $status = $tts.Status.ToString()
                if ($status -eq 'NotRunning') {
                    $tts.StartHost()
                    Wait-HostStart $tts
                    $tts.Connect()
                    $script:lastMasterControl = $null
                }
                Send-Ok @{ version = $script:ttsVersion; status = $status }
            } catch {
                Send-Err "keepalive failed: $_"
            }
        }

    } elseif ($msg.type -eq 'quit') {
        if ($null -ne $tts) {
            try {
                $tts.Disconnect()
            } catch {}
        }
        Send-Ok $null
        exit 0

    } else {
        Send-Err "Unknown message type: $($msg.type)"
    }
}

if ($null -ne $tts) {
    try {
        $tts.Disconnect()
    } catch {}
}
