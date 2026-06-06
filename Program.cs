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

app.Run();
