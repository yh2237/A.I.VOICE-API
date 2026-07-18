using System.Diagnostics;
using System.Security.Cryptography;

namespace AIVoiceApi.Services;

public sealed class AiVoiceService : IHostedService, IDisposable
{
    private readonly string _dllPath;
    private readonly string _targetHostName;
    private readonly string _hostProcessName;
    private readonly string _editorPath;
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

    private bool IsConnectionHealthy()
    {
        if (!Ready || _tts == null) return false;
        try
        {
            var status = (string)_tts.Status.ToString();
            return status != "NotRunning" && status != "NotConnected";
        }
        catch
        {
            return false;
        }
    }

    private async Task RecoverAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _restartBusy, 1, 0) != 0) return;

        try
        {
            for (int attempt = 1; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    await ConnectAsync();
                    _logger.LogInformation(
                        "A.I.VOICE connected. Host: {Host}, Presets: {Presets}",
                        CurrentHostName, string.Join(", ", PresetNames));
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Connect failed (attempt {Attempt}); restarting editor process", attempt);
                }

                try
                {
                    await TerminateHostAsync();
                    StartEditorProcess();
                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                    await ConnectAsync();
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

    public async Task ConnectAsync()
    {
        await DisconnectAsync();

        var (tts, hostName) = CreateTtsControl();

        if (tts.Status.ToString() == "NotRunning")
        {
            try { tts.StartHost(); } catch { }
            await WaitWhileNotRunningAsync(tts, 30);

            if (tts.Status.ToString() == "NotRunning")
            {
                StartEditorProcess();
                await WaitWhileNotRunningAsync(tts, 75);
            }
        }

        tts.Connect();

        _tts = tts;
        Ready = true;
        CurrentHostName = hostName;
        Version = tts.Version.ToString();

        var presetArray = (string[])tts.VoicePresetNames;
        PresetNames = presetArray.ToList();
        _lastPresetName = null;
        _lastMasterControl = null;
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
            await ConnectWithRetryAsync();
            _logger.LogInformation("A.I.VOICE Editor restarted. Host: {Host}", CurrentHostName);
        }
        finally
        {
            _restartBusy = 0;
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

            var status = (string)tts!.Status.ToString();
            if (status != "NotRunning")
            {
                if (status == "NotConnected")
                {
                    try { tts.Connect(); } catch { }
                }

                tts.TerminateHost();

                for (int i = 0; i < 75; i++)
                {
                    await Task.Delay(200);
                    if (tts.Status.ToString() == "NotRunning") break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Graceful host termination failed; falling back to process kill");
        }

        await KillRemainingHostProcessesAsync();
    }

    private static async Task WaitWhileNotRunningAsync(dynamic tts, int iterations)
    {
        for (int i = 0; i < iterations; i++)
        {
            await Task.Delay(200);
            if (tts.Status.ToString() != "NotRunning") break;
        }
    }

    private void StartEditorProcess()
    {
        var existing = Process.GetProcessesByName(_hostProcessName);
        var alreadyRunning = existing.Length > 0;
        foreach (var p in existing) p.Dispose();
        if (alreadyRunning) return;

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
        foreach (var proc in Process.GetProcessesByName(_hostProcessName))
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
    }

    private async Task ConnectWithRetryAsync(int attempts = 5, int delayMs = 2000)
    {
        for (int i = 1; ; i++)
        {
            try
            {
                await ConnectAsync();
                return;
            }
            catch (Exception ex) when (i < attempts)
            {
                _logger.LogWarning(ex, "Connect attempt {Attempt}/{Total} failed; retrying", i, attempts);
                await Task.Delay(delayMs);
            }
        }
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
        if (_tts == null || !Ready)
            throw new InvalidOperationException("A.I.VOICE not connected");

        var preset = p.Preset ?? PresetNames.FirstOrDefault()
            ?? throw new InvalidOperationException("No preset available");

        if (preset != _lastPresetName)
        {
            _tts.CurrentVoicePresetName = preset;
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
            _tts.MasterControl = masterControl;
            _lastMasterControl = masterControl;
        }

        _tts.Text = p.Text;

        var tmpPath = Path.Combine(Path.GetTempPath(),
            $"aivoice_synth_{RandomNumberGenerator.GetHexString(8, true)}.wav");
        _tts.SaveAudioToFile(tmpPath);

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
