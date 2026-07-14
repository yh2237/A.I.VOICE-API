using System.Diagnostics;
using System.Security.Cryptography;

namespace AIVoiceApi.Services;

public sealed class AiVoiceService : IHostedService, IDisposable
{
    private readonly string _dllPath;
    private readonly string _targetHostName;
    private readonly ILogger<AiVoiceService> _logger;
    private readonly SynthesisQueue _queue = new();

    private dynamic? _tts;
    private Timer? _keepaliveTimer;
    private CancellationTokenSource? _stopCts;
    private string? _lastPresetName;
    private string? _lastMasterControl;
    private volatile int _synthBusy;

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

        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _stopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            await ConnectAsync();
            _logger.LogInformation(
                "A.I.VOICE connected. Host: {Host}, Presets: {Presets}",
                CurrentHostName, string.Join(", ", PresetNames));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not connect to A.I.VOICE on startup. Use POST /api/reconnect to retry.");
        }

        _keepaliveTimer = new Timer(_ => _ = DoKeepaliveAsync(), null,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _stopCts?.Cancel();
        if (_keepaliveTimer != null)
        {
            await _keepaliveTimer.DisposeAsync();
        }

        await DisconnectAsync();
    }

    public async Task ConnectAsync()
    {
        await DisconnectAsync();

        var ttsType = Type.GetTypeFromProgID("AI.Talk.Editor.Api.TtsControl")
            ?? throw new InvalidOperationException(
                "COM ProgID 'AI.Talk.Editor.Api.TtsControl' not found. Is A.I.VOICE Editor installed?");
        dynamic tts = Activator.CreateInstance(ttsType)!;

        string[] hosts = tts.GetAvailableHostNames();
        if (hosts == null || hosts.Length == 0)
            throw new InvalidOperationException("No available hosts. Make sure A.I.VOICE Editor is running.");

        var hostName = string.IsNullOrEmpty(_targetHostName) ? hosts[0] : _targetHostName;
        tts.Initialize(hostName);

        if (tts.Status.ToString() == "NotRunning")
        {
            tts.StartHost();
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(200);
                if (tts.Status.ToString() != "NotRunning") break;
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

    private async Task DoKeepaliveAsync()
    {
        if (!Ready || _tts == null) return;
        try
        {
            var status = _tts.Status.ToString();
            if (status == "NotRunning")
            {
                _tts.StartHost();
                for (int i = 0; i < 30; i++)
                {
                    await Task.Delay(200);
                    if (_tts.Status.ToString() != "NotRunning") break;
                }
                _tts.Connect();
                _lastMasterControl = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Keepalive failed");
        }
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
        _keepaliveTimer?.Dispose();
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
