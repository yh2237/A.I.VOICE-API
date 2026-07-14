using AIVoiceApi.Services;

var builder = WebApplication.CreateBuilder(args);

var port = Environment.GetEnvironmentVariable("PORT")
    ?? builder.Configuration["Server:Port"] ?? "58080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddSingleton<AiVoiceService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AiVoiceService>());

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/api/status", (AiVoiceService svc) => Results.Ok(new
{
    connected = svc.Ready,
    hostName = svc.CurrentHostName,
    version = svc.Version,
    presetNames = svc.PresetNames,
}));

app.MapGet("/api/presets", (AiVoiceService svc) =>
{
    if (!svc.Ready)
        return Results.Json(new { error = "A.I.VOICE not connected" }, statusCode: 503);
    return Results.Ok(new { presets = svc.PresetNames });
});

app.MapPost("/api/reconnect", async (AiVoiceService svc) =>
{
    try
    {
        await svc.ConnectAsync();
        return Results.Ok(new
        {
            connected = true,
            hostName = svc.CurrentHostName,
            presetNames = svc.PresetNames,
        });
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 503);
    }
});

app.MapPost("/api/synthesize", async (AiVoiceService svc, HttpRequest req, CancellationToken ct) =>
{
    if (!svc.Ready)
        return Results.Json(new { error = "A.I.VOICE not connected" }, statusCode: 503);

    SynthesisParams? body;
    try
    {
        body = await req.ReadFromJsonAsync<SynthesisParams>(cancellationToken: ct);
    }
    catch
    {
        return Results.Json(new { error = "Invalid JSON" }, statusCode: 400);
    }

    if (body == null || string.IsNullOrEmpty(body.Text))
        return Results.Json(new { error = "Missing or invalid \"text\" field" }, statusCode: 400);

    try
    {
        var wav = await svc.SynthesizeAsync(body, ct);
        return Results.Bytes(wav, "audio/wav");
    }
    catch (OperationCanceledException)
    {
        return Results.Json(new { error = "Request cancelled" }, statusCode: 499);
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 500);
    }
});

app.MapPost("/api/synthesize/benchmark", async (AiVoiceService svc, HttpRequest req, CancellationToken ct) =>
    await HandleBenchmarkAsync(svc, req, ct));

app.MapGet("/api/synthesize/benchmark", async (AiVoiceService svc, HttpRequest req, CancellationToken ct) =>
    await HandleBenchmarkAsync(svc, req, ct));

async Task<IResult> HandleBenchmarkAsync(AiVoiceService svc, HttpRequest req, CancellationToken ct)
{
    if (!svc.Ready)
        return Results.Json(new { error = "A.I.VOICE not connected" }, statusCode: 503);

    SynthesisParams body;
    if (HttpMethods.IsPost(req.Method))
    {
        SynthesisParams? parsed;
        try
        {
            parsed = await req.ReadFromJsonAsync<SynthesisParams>(cancellationToken: ct);
        }
        catch
        {
            return Results.Json(new { error = "Invalid JSON" }, statusCode: 400);
        }
        if (parsed == null || string.IsNullOrEmpty(parsed.Text))
            return Results.Json(new { error = "Missing or invalid \"text\" field" }, statusCode: 400);
        body = parsed;
    }
    else
    {
        var q = req.Query;
        var text = q["text"].FirstOrDefault();
        if (string.IsNullOrEmpty(text))
            return Results.Json(new { error = "Missing or invalid \"text\" field" }, statusCode: 400);

        body = new SynthesisParams
        {
            Text = text,
            Preset = q["preset"].FirstOrDefault(),
            Speed = double.TryParse(q["speed"], out var s) ? s : 1.0,
            Pitch = double.TryParse(q["pitch"], out var p) ? p : 1.0,
            PitchRange = double.TryParse(q["pitchRange"], out var pr) ? pr : 1.0,
            Volume = double.TryParse(q["volume"], out var v) ? v : 1.0,
            MiddlePause = int.TryParse(q["middlePause"], out var mp) ? mp : 150,
            LongPause = int.TryParse(q["longPause"], out var lp) ? lp : 370,
            SentencePause = int.TryParse(q["sentencePause"], out var sp) ? sp : 800,
            Priority = int.TryParse(q["priority"], out var pri) ? pri : 0,
        };
    }

    try
    {
        var result = await svc.SynthesizeBenchmarkAsync(body, ct);
        return Results.Json(new
        {
            elapsedMs = result.ElapsedMs,
            queueWaitMs = result.QueueWaitMs,
            synthMs = result.SynthMs,
            hardware = new
            {
                cpuName = result.Hardware.CpuName,
                cpuCores = result.Hardware.CpuCores,
                osDescription = result.Hardware.OsDescription,
                architecture = result.Hardware.Architecture,
            },
        });
    }
    catch (OperationCanceledException)
    {
        return Results.Json(new { error = "Request cancelled" }, statusCode: 499);
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 500);
    }
}

app.Run();
