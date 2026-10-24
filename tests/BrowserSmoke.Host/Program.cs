using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseStaticWebAssets();
var app = builder.Build();
var received = new ConcurrentDictionary<string, int>();
bool unavailable = false;
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.MapGet("/probe", (HttpContext context) => Results.Text(context.Request.Headers.TraceParent.ToString()));
app.MapGet("/received", () => received);
app.MapPost("/reset", () => { received.Clear(); unavailable = false; return Results.NoContent(); });
app.MapPost("/unavailable", () => { unavailable = true; return Results.NoContent(); });
app.MapPost("/telemetry/v1/{signal}", async (string signal, HttpContext context) =>
{
    if (unavailable)
    {
        return Results.StatusCode(503);
    }
    await using var body = new MemoryStream();
    await context.Request.Body.CopyToAsync(body, context.RequestAborted);
    received.AddOrUpdate(signal, (int)body.Length, (_, current) => current + (int)body.Length);
    return Results.Bytes([], "application/x-protobuf");
});
app.MapFallbackToFile("index.html");
await app.RunAsync();
