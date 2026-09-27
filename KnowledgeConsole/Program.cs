using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.Features;
using QnocrPoc.Knowledge;
using QnocrPoc.Models;

var stateDirectory = Path.GetFullPath(Path.Combine("Output", "KnowledgeConsole"));
var port = 18724;
for (var index = 0; index < args.Length; index += 2)
{
    if (index + 1 >= args.Length) throw new ArgumentException("Use --state-dir DIR [--port NUMBER].");
    if (args[index] == "--state-dir") stateDirectory = Path.GetFullPath(args[index + 1]);
    else if (args[index] == "--port" && int.TryParse(args[index + 1], out var parsed) && parsed is >= 1024 and <= 65535)
        port = parsed;
    else throw new ArgumentException("Use --state-dir DIR [--port NUMBER].");
}
var token = Environment.GetEnvironmentVariable("QN_KNOWLEDGE_CONSOLE_TOKEN");
if (string.IsNullOrWhiteSpace(token) || token.Length < 24 || !token.All(Uri.IsHexDigit))
    token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
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
builder.Services.Configure<FormOptions>(options => { options.ValueCountLimit = 64; options.ValueLengthLimit = 8000; });
var app = builder.Build();
var review = new KnowledgeReviewService(stateDirectory);
var authoring = new KnowledgeAuthoringService(stateDirectory, review);
var revisions = new KnowledgeRevisionService(stateDirectory, review);
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'";
    if (context.Request.Query["token"] != token)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("Knowledge Console access token required.");
        return;
    }
    try { await next(); }
    catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException or
        IOException or UnauthorizedAccessException or KeyNotFoundException or System.Text.Json.JsonException)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(KnowledgeConsolePages.Error(ex.Message, token));
    }
});
app.MapGet("/", () => Results.Content(KnowledgeConsolePages.Home(review.Snapshot(),
    authoring.ListDrafts(), revisions.ListRevisions(), token), "text/html; charset=utf-8"));
app.MapPost("/draft", async (HttpContext context) =>
{
    var form = await context.Request.ReadFormAsync();
    var input = KnowledgeConsolePages.ParseDraft(form);
    authoring.Create(input, form["actor"].ToString());
    return Results.Redirect("/?token=" + token);
});
app.MapPost("/item/{id}/edit", async (string id, HttpContext context) =>
{
    var form = await context.Request.ReadFormAsync();
    review.EditDraft(id, form["value"].ToString(), form["actor"].ToString());
    return Results.Redirect("/?token=" + token);
});
app.MapPost("/item/{id}/submit", async (string id, HttpContext context) =>
{
    var form = await context.Request.ReadFormAsync();
    review.SubmitForReview(id, form["actor"].ToString());
    return Results.Redirect("/?token=" + token);
});
app.MapPost("/item/{id}/approve", async (string id, HttpContext context) =>
{
    var form = await context.Request.ReadFormAsync();
    review.Approve(id, form["actor"].ToString());
    return Results.Redirect("/?token=" + token);
});
app.MapPost("/item/{id}/reject", async (string id, HttpContext context) =>
{
    var form = await context.Request.ReadFormAsync();
    review.Reject(id, form["actor"].ToString(), form["reason"].ToString());
    return Results.Redirect("/?token=" + token);
});
app.MapPost("/item/{id}/publish", async (string id, HttpContext context) =>
{
    var form = await context.Request.ReadFormAsync();
    if (form["confirm"] != "publish") throw new InvalidOperationException("发布需勾选人工确认。");
    review.Publish(id, form["actor"].ToString(), allowAiUse: true);
    return Results.Redirect("/?token=" + token);
});
app.MapPost("/revision", async (HttpContext context) =>
{
    var form = await context.Request.ReadFormAsync();
    var source = new KnowledgeSource(KnowledgeSourceType.ManualInput, "KnowledgeConsole",
        form["sourceReference"].ToString(), DateTimeOffset.UtcNow);
    revisions.Create(form["sellerId"].ToString(), form["productId"].ToString(),
        form["key"].ToString(), form["newValue"].ToString(), form["reason"].ToString(),
        form["version"].ToString(), source, form["actor"].ToString());
    return Results.Redirect("/?token=" + token);
});
app.MapPost("/revision/{id}/publish", async (string id, HttpContext context) =>
{
    var form = await context.Request.ReadFormAsync();
    if (form["confirm"] != "publish") throw new InvalidOperationException("修订发布需勾选人工确认。");
    revisions.Publish(id, form["actor"].ToString());
    return Results.Redirect("/?token=" + token);
});
Console.WriteLine($"[Knowledge Console] http://127.0.0.1:{port}/?token={token}");
Console.WriteLine($"State: {stateDirectory}; test knowledge only; no AI call or send operation.");
await app.RunAsync();
