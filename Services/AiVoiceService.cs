using System.Diagnostics;
using System.Security.Cryptography;

namespace AIVoiceApi.Services;

public sealed class AiVoiceService : IHostedService, IDisposable
{
    private readonly string _dllPath;
    private readonly string _targetHostName;
    private readonly string _hostProcessName;
    private readonly string _editorPath;
    private readonly int _startupTimeoutSec;
    private readonly ILogger<AiVoiceService> _logger;
    private readonly SynthesisQueue _queue = new();

    private dynamic? _tts;
    private Task? _supervisorTask;
    private CancellationTokenSource? _stopCts;
    private string? _lastPresetName;
    private string? _lastMasterControl;
    private volatile int _synthBusy;
    private volatile int _restartBusy;

    public bool Ready { get; private set; }
    public bool IsRestarting => _restartBusy != 0;
    public string? CurrentHostName { get; private set; }
    public string? Version { get; private set; }
    public IReadOnlyList<string> PresetNames { get; private set; } = Array.Empty<string>();

    public AiVoiceService(IConfiguration config, ILogger<AiVoiceService> logger)
    {
        _dllPath = Environment.GetEnvironmentVariable("AIVOICE_DLL_PATH")
            ?? config["AiVoice:DllPath"]
            ?? @"C:\Program Files\AI\AIVoice\AIVoiceEditor\AI.Talk.Editor.Api.dll";

        _targetHostName = Environment.GetEnvironmentVariable("AIVOICE_HOST_NAME")
            ?? config["AiVoice:HostName"]
            ?? "";

        _hostProcessName = Environment.GetEnvironmentVariable("AIVOICE_PROCESS_NAME")
            ?? config["AiVoice:ProcessName"]
            ?? "AIVoiceEditor";

        _editorPath = Environment.GetEnvironmentVariable("AIVOICE_EDITOR_PATH")
            ?? config["AiVoice:EditorPath"]
            ?? @"C:\Program Files\AI\AIVoice\AIVoiceEditor\AIVoiceEditor.exe";

        _startupTimeoutSec = int.TryParse(
            Environment.GetEnvironmentVariable("AIVOICE_STARTUP_TIMEOUT_SEC")
                ?? config["AiVoice:StartupTimeoutSec"], out var sts) && sts > 0
            ? sts
            : 120;

        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _stopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _supervisorTask = Task.Run(() => SupervisorLoopAsync(_stopCts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _stopCts?.Cancel();
        if (_supervisorTask != null)
        {
            try { await _supervisorTask.WaitAsync(TimeSpan.FromSeconds(5), ct); } catch { }
        }

        await DisconnectAsync();
    }

    private async Task SupervisorLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_restartBusy == 0 && !IsConnectionHealthy())
                {
                    await RecoverAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Supervisor iteration failed");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private const int StatusNotRunning = 0;
    private const int StatusNotConnected = 1;
    private const int StatusIdle = 2;
    private const int StatusBusy = 3;

    private bool IsConnectionHealthy()
    {
        if (!Ready || _tts == null) return false;
        var s = GetStatusInt(_tts);
        return s == StatusIdle || s == StatusBusy;
    }

    private async Task RecoverAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _restartBusy, 1, 0) != 0) return;

        try
        {
            if (_tts != null && IsConnectionHealthy())
                return;

            for (int attempt = 1; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    await ConnectAsync(ct);
                    _logger.LogInformation(
                        "A.I.VOICE connected. Host: {Host}, Presets: {Presets}",
                        CurrentHostName, string.Join(", ", PresetNames));
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Connect failed (attempt {Attempt})", attempt);
                }

                try
                {
                    _logger.LogWarning("Forcing editor restart (attempt {Attempt})", attempt);
                    await TerminateHostAsync();
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    await ConnectAsync(ct);
                    _logger.LogInformation(
                        "A.I.VOICE Editor restarted and connected. Host: {Host}, Presets: {Presets}",
                        CurrentHostName, string.Join(", ", PresetNames));
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Editor restart failed (attempt {Attempt})", attempt);
                }

                await Task.Delay(TimeSpan.FromSeconds(Math.Min(5 * attempt, 60)), ct);
            }
        }
        finally
        {
            _restartBusy = 0;
        }
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await DisconnectAsync();

        var (tts, hostName) = CreateTtsControl();

        if (GetStatusInt(tts) == StatusNotRunning)
        {
            _logger.LogInformation("Editor not running. Starting via StartHost...");
            try { tts.StartHost(); } catch (Exception ex) { _logger.LogDebug(ex, "StartHost failed"); }
            await WaitWhileStatusAsync(tts, StatusNotRunning, negate: true, timeout: TimeSpan.FromSeconds(15), ct: ct);

            if (GetStatusInt(tts) == StatusNotRunning)
            {
                _logger.LogInformation("StartHost had no effect. Starting editor executable...");
                StartEditorProcess();
                await WaitWhileStatusAsync(tts, StatusNotRunning, negate: true, timeout: TimeSpan.FromSeconds(30), ct: ct);

                if (GetStatusInt(tts) == StatusNotRunning)
                    throw new InvalidOperationException(
                        "A.I.VOICE Editor process did not start. Check AiVoice:EditorPath.");
            }
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(_startupTimeoutSec);
        var loggedWaiting = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                tts.Connect();
                break;
            }
            catch (Exception ex)
            {
                if (GetStatusInt(tts) == StatusNotRunning)
                    throw new InvalidOperationException(
                        "A.I.VOICE Editor exited while waiting for connection.", ex);

                if (DateTimeOffset.UtcNow >= deadline)
                    throw new TimeoutException(
                        $"Could not connect to A.I.VOICE Editor within {_startupTimeoutSec}s. " +
                        "The editor may be stuck or blocked by a dialog.", ex);

                if (!loggedWaiting)
                {
                    loggedWaiting = true;
                    _logger.LogInformation(
                        "Editor is starting up. Retrying connection for up to {Timeout}s...",
                        _startupTimeoutSec);
                }
                await Task.Delay(2000, ct);
            }
        }

        _tts = tts;
        Ready = true;
        CurrentHostName = hostName;
        Version = SafeGet(tts, "Version");

        var presetArray = SafeGetArray(tts, "VoicePresetNames");
        PresetNames = presetArray?.ToList() ?? new List<string>();
        _lastPresetName = null;
        _lastMasterControl = null;
    }

    private static string? SafeGet(dynamic tts, string prop)
    {
        try { return tts.GetType().GetProperty(prop)?.GetValue(tts)?.ToString(); }
        catch { return null; }
    }

    private static string[]? SafeGetArray(dynamic tts, string prop)
    {
        try { return tts.GetType().GetProperty(prop)?.GetValue(tts) as string[]; }
        catch { return null; }
    }

    public async Task RestartHostAsync()
    {
        if (Interlocked.CompareExchange(ref _restartBusy, 1, 0) != 0)
            throw new InvalidOperationException("Restart already in progress");

        try
        {
            _logger.LogInformation("Restarting A.I.VOICE Editor...");
            Ready = false;
            await TerminateHostAsync();
            await Task.Delay(TimeSpan.FromSeconds(2));
            await ConnectAsync();
            _logger.LogInformation("A.I.VOICE Editor restarted. Host: {Host}", CurrentHostName);
        }
        finally
        {
            _restartBusy = 0;
        }
    }

    private static int GetStatusInt(dynamic tts)
    {
        try
        {
            object raw = tts.Status;
            if (raw is int i) return i;
            var s = raw.ToString()!;
            return s switch
            {
                "NotRunning" => 0,
                "NotConnected" => 1,
                "Idle" => 2,
                "Busy" => 3,
                _ => int.TryParse(s, out var n) ? n : -1,
            };
        }
        catch
        {
            return -1;
        }
    }

    private (dynamic Tts, string HostName) CreateTtsControl()
    {
        var ttsType = Type.GetTypeFromProgID("AI.Talk.Editor.Api.TtsControl")
            ?? throw new InvalidOperationException(
                "COM ProgID 'AI.Talk.Editor.Api.TtsControl' not found. Is A.I.VOICE Editor installed?");
        dynamic tts = Activator.CreateInstance(ttsType)!;

        string[] hosts = tts.GetAvailableHostNames();
        if (hosts == null || hosts.Length == 0)
            throw new InvalidOperationException("No available hosts. Make sure A.I.VOICE Editor is running.");

        var hostName = string.IsNullOrEmpty(_targetHostName) ? hosts[0] : _targetHostName;
        tts.Initialize(hostName);
        return (tts, hostName);
    }

    private async Task TerminateHostAsync()
    {
        dynamic? tts = _tts;
        _tts = null;

        try
        {
            if (tts == null)
            {
                (tts, _) = CreateTtsControl();
            }

            var status = GetStatusInt(tts!);
            if (status != StatusNotRunning && status >= 0)
            {
                if (status == StatusNotConnected)
                {
                    try { tts.Connect(); } catch { }
                }

                _logger.LogInformation("Sending TerminateHost to editor...");
                tts.TerminateHost();

                for (int i = 0; i < 75; i++)
                {
                    await Task.Delay(200);
                    if (GetStatusInt(tts) == StatusNotRunning) break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Graceful host termination failed; falling back to process kill");
        }

        await KillRemainingHostProcessesAsync();
    }

    private static async Task WaitWhileStatusAsync(dynamic tts, int targetStatus, bool negate, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var s = GetStatusInt(tts);
            if (negate ? s != targetStatus : s == targetStatus) return;
            await Task.Delay(500, ct);
        }
    }

    private void StartEditorProcess()
    {
        var existing = Process.GetProcessesByName(_hostProcessName);
        try
        {
            var alive = existing.Where(p =>
            {
                try { return !p.HasExited; } catch { return false; }
            }).ToList();

            if (alive.Count > 0)
            {
                _logger.LogDebug(
                    "Editor process already running ({Count} alive), not starting",
                    alive.Count);
                return;
            }
        }
        finally
        {
            foreach (var p in existing) p.Dispose();
        }

        if (!File.Exists(_editorPath))
            throw new InvalidOperationException($"A.I.VOICE Editor executable not found: {_editorPath}");

        _logger.LogInformation("Starting A.I.VOICE Editor process: {Path}", _editorPath);
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = _editorPath,
            WorkingDirectory = Path.GetDirectoryName(_editorPath) ?? "",
            UseShellExecute = true,
        });
    }

    private async Task KillRemainingHostProcessesAsync()
    {
        for (int retry = 0; retry < 3; retry++)
        {
            var procs = Process.GetProcessesByName(_hostProcessName);
            if (procs.Length == 0) { foreach (var p in procs) p.Dispose(); return; }

            foreach (var proc in procs)
            {
                try
                {
                    _logger.LogWarning("Force killing {Name} (PID {Pid})", proc.ProcessName, proc.Id);
                    proc.Kill(entireProcessTree: true);
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await proc.WaitForExitAsync(cts.Token);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to kill {Name} (PID {Pid})", proc.ProcessName, proc.Id);
                }
                finally
                {
                    proc.Dispose();
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        _logger.LogWarning("Some editor processes may still be alive after kill attempts");
    }

    private async Task DisconnectAsync()
    {
        Ready = false;
        if (_tts != null)
        {
            try { _tts.Disconnect(); } catch { }
            _tts = null;
        }
        await Task.CompletedTask;
    }

    public Task<byte[]> SynthesizeAsync(SynthesisParams p, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => tcs.TrySetCanceled(ct));
        _queue.Enqueue(p, tcs);
        _ = ProcessQueueLoopAsync();
        return tcs.Task;
    }

    public async Task<SynthesisBenchmarkResult> SynthesizeBenchmarkAsync(SynthesisParams p, CancellationToken ct = default)
    {
        var totalSw = Stopwatch.StartNew();
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => tcs.TrySetCanceled(ct));
        var item = _queue.Enqueue(p, tcs);
        _ = ProcessQueueLoopAsync();
        var wav = await tcs.Task;
        totalSw.Stop();
        return new SynthesisBenchmarkResult
        {
            Wav = wav,
            ElapsedMs = totalSw.ElapsedMilliseconds,
            QueueWaitMs = item.QueueWaitMs,
            SynthMs = item.SynthMs,
            Hardware = HardwareInfo.Current,
        };
    }

    private async Task ProcessQueueLoopAsync()
    {
        if (Interlocked.CompareExchange(ref _synthBusy, 1, 0) != 0) return;

        try
        {
            while (true)
            {
                var item = _queue.TryDequeue();
                if (item == null) break;

                item.QueueWaitMs = (long)(DateTimeOffset.UtcNow - item.EnqueuedAt).TotalMilliseconds;

                try
                {
                    var synthSw = Stopwatch.StartNew();
                    var wav = await DoSynthesizeAsync(item.Params);
                    synthSw.Stop();
                    item.SynthMs = synthSw.ElapsedMilliseconds;
                    item.Tcs.TrySetResult(wav);
                }
                catch (Exception ex)
                {
                    item.Tcs.TrySetException(ex);
                }
            }
        }
        finally
        {
            _synthBusy = 0;
            if (_queue.HasItems)
            {
                _ = ProcessQueueLoopAsync();
            }
        }
    }

    private async Task<byte[]> DoSynthesizeAsync(SynthesisParams p)
    {
        dynamic? tts = _tts;
        if (tts == null || !Ready)
            throw new InvalidOperationException("A.I.VOICE not connected");

        var preset = p.Preset ?? PresetNames.FirstOrDefault()
            ?? throw new InvalidOperationException("No preset available");

        if (preset != _lastPresetName)
        {
            tts.CurrentVoicePresetName = preset;
            _lastPresetName = preset;
        }

        var masterControl = System.Text.Json.JsonSerializer.Serialize(new
        {
            Volume = p.Volume,
            Speed = p.Speed,
            Pitch = p.Pitch,
            PitchRange = p.PitchRange,
            MiddlePause = p.MiddlePause,
            LongPause = p.LongPause,
            SentencePause = p.SentencePause,
        });

        if (masterControl != _lastMasterControl)
        {
            tts.MasterControl = masterControl;
            _lastMasterControl = masterControl;
        }

        tts.Text = p.Text;

        var tmpPath = Path.Combine(Path.GetTempPath(),
            $"aivoice_synth_{RandomNumberGenerator.GetHexString(8, true)}.wav");
        tts.SaveAudioToFile(tmpPath);

        var wav = await File.ReadAllBytesAsync(tmpPath);
        try { File.Delete(tmpPath); } catch { }
        return wav;
    }

    public void Dispose()
    {
        _stopCts?.Dispose();
    }
}

public class SynthesisBenchmarkResult
{
    public required byte[] Wav { get; init; }
    public required long ElapsedMs { get; init; }
    public required long QueueWaitMs { get; init; }
    public required long SynthMs { get; init; }
    public required HardwareInfo Hardware { get; init; }
}

public record HardwareInfo
{
    public string? CpuName { get; init; }
    public int CpuCores { get; init; }
    public string? OsDescription { get; init; }
    public string? Architecture { get; init; }

    private static HardwareInfo? _current;
    private static readonly object _lock = new();

    public static HardwareInfo Current
    {
        get
        {
            if (_current != null) return _current;
            lock (_lock)
            {
                if (_current != null) return _current;
                _current = Load();
            }
            return _current;
        }
    }

    private static HardwareInfo Load()
    {
        var cpuName = GetCpuName();

        return new HardwareInfo
        {
            CpuName = cpuName,
            CpuCores = Environment.ProcessorCount,
            OsDescription = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
        };
    }

    private static string? GetCpuName()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key?.GetValue("ProcessorNameString") is string name && !string.IsNullOrWhiteSpace(name))
                return name.Trim();
        }
        catch { }

        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
    }
}
