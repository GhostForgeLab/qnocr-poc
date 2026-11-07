using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.Features;
using QnocrPoc.Models;
using QnocrPoc.Product;
using QnocrPoc.Shadow;

string? state = null, run = null;
string? demoRoot = null;
var port = 18730;
for (var index = 0; index < args.Length; index += 2)
{
    if (index + 1 >= args.Length) throw new ArgumentException("Use --state-dir DIR --run-dir RUN [--port NUMBER].");
    if (args[index] == "--state-dir") state = Path.GetFullPath(args[index + 1]);
    else if (args[index] == "--demo-root") demoRoot = Path.GetFullPath(args[index + 1]);
    else if (args[index] == "--run-dir") run = Path.GetFullPath(args[index + 1]);
    else if (args[index] == "--port" && int.TryParse(args[index + 1], out var parsed) && parsed is >= 1024 and <= 65535)
        port = parsed;
    else throw new ArgumentException("Use --state-dir DIR --run-dir RUN [--port NUMBER].");
}
if (demoRoot is not null)
{
    if (state is not null || run is not null) throw new ArgumentException("Demo requires its own state and run directories.");
    (state, run) = await AgentConsoleDemo.CreateAsync(demoRoot);
}
if (state is null || run is null) throw new ArgumentException("Both --state-dir and --run-dir are required, or use --demo-root DIR.");
var accessToken = Environment.GetEnvironmentVariable("QN_AGENT_CONSOLE_TOKEN");
if (string.IsNullOrWhiteSpace(accessToken) || accessToken.Length < 24 || !accessToken.All(Uri.IsHexDigit))
    accessToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
var builder = WebApplication.CreateBuilder(Array.Empty<string>());
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = 64 * 1024;
});
builder.Services.Configure<FormOptions>(options => { options.ValueCountLimit = 32; options.ValueLengthLimit = 8000; });
var app = builder.Build();
var read = new AgentConsoleReadService(state, run);
var assistant = new AssistantReplyService(state);
var manual = new ManualProductResolver(state);
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'";
    if (context.Request.Query["token"] != accessToken)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("Agent Console access token required.");
        return;
    }
    try { await next(); }
    catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException or
        IOException or UnauthorizedAccessException or KeyNotFoundException or System.Text.Json.JsonException)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(AgentConsolePages.Error(ex.Message, accessToken));
    }
});
app.MapGet("/", () => Results.Content(AgentConsolePages.Home(read.ListSessions(), accessToken, demoRoot is not null),
    "text/html; charset=utf-8"));
app.MapGet("/session/{id}", (string id) => Results.Content(
    AgentConsolePages.Conversation(read.GetConversation(id), accessToken), "text/html; charset=utf-8"));
app.MapPost("/preview/{id}/edit", async (string id, HttpContext context) =>
{
    var sessionId = read.RequirePreviewSession(id);
    var form = await context.Request.ReadFormAsync();
    assistant.Edit(id, form["operator"].ToString(), form["reply"].ToString());
    return Results.Redirect(AgentConsolePages.SessionUrl(sessionId, accessToken));
});
app.MapPost("/preview/{id}/approve", async (string id, HttpContext context) =>
{
    var sessionId = read.RequirePreviewSession(id);
    var form = await context.Request.ReadFormAsync();
    assistant.Approve(id, form["operator"].ToString(), form["replyHash"].ToString());
    return Results.Redirect(AgentConsolePages.SessionUrl(sessionId, accessToken));
});
app.MapPost("/preview/{id}/copy", async (string id, HttpContext context) =>
{
    var sessionId = read.RequirePreviewSession(id);
    var form = await context.Request.ReadFormAsync();
    assistant.Copy(id, form["operator"].ToString(), form["replyHash"].ToString(),
        new WindowsAssistantClipboard());
    return Results.Redirect(AgentConsolePages.SessionUrl(sessionId, accessToken));
});
app.MapPost("/preview/{id}/feedback", async (string id, HttpContext context) =>
{
    var sessionId = read.RequirePreviewSession(id);
    var form = await context.Request.ReadFormAsync();
    var kind = form["type"].ToString();
    var final = kind == ReplyFeedbackTypes.Rejected ? form["finalText"].ToString() : null;
    var reason = form["reason"].ToString();
    assistant.RecordFeedback(id, form["operator"].ToString(), kind,
        rejectedFinalReply: final, reason: string.IsNullOrWhiteSpace(reason) ? null : reason);
    return Results.Redirect(AgentConsolePages.SessionUrl(sessionId, accessToken));
});
app.MapPost("/session/{id}/clear-product", async (string id, HttpContext context) =>
{
    read.RequireVisibleSession(id);
    var conversation = read.GetConversation(id);
    if (!conversation.CanClearProduct)
        throw new InvalidOperationException("This capture exposes a read-only product snapshot.");
    if (conversation.Session.CurrentProductContext?.Source != ProductContextSource.Manual)
        throw new InvalidOperationException("Only a manually bound current product can be cleared here.");
    var form = await context.Request.ReadFormAsync();
    manual.Clear(id, form["operator"].ToString());
    return Results.Redirect(AgentConsolePages.SessionUrl(id, accessToken));
});
Console.WriteLine($"[Agent Console] http://127.0.0.1:{port}/?token={accessToken}");
Console.WriteLine($"Run: {run}; State: {state}; Send=OFF; clipboard only.");
await app.RunAsync();
