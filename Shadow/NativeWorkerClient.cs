using System.Diagnostics;
using System.Text.Json;
using QnocrPoc.Models;

namespace QnocrPoc.Shadow;

public sealed class NativeWorkerClient
{
    public async Task<CaptureResult> CaptureAsync(QianniuWindow window, string directory, CancellationToken token)
    {
        var result = Path.Combine(directory, "capture-result.json");
        var ok = await RunAsync(["--capture-worker", $"{window.Hwnd:X}", window.Pid.ToString(), directory], 20, token);
        return ok && File.Exists(result) ? JsonSerializer.Deserialize<CaptureResult>(await File.ReadAllTextAsync(result, token))!
            : new(false, null, "Isolated PrintWindow worker", "Worker failed or timed out; next frame will rediscover window.", TimedOut: true);
    }
    public async Task<OcrResult> RecognizeAsync(string image, CancellationToken token)
    {
        var result = image + ".worker.json";
        if (!await RunAsync(["--ocr-worker", image, result], 45, token) || !File.Exists(result)) throw new TimeoutException("OCR worker failed/timed out; no stale result used.");
        return JsonSerializer.Deserialize<OcrResult>(await File.ReadAllTextAsync(result, token))!;
    }
    private static async Task<bool> RunAsync(string[] arguments, int seconds, CancellationToken token)
    {
        // Use our apphost so PerMonitorV2 in app.manifest applies to GetWindowRect/PrintWindow.
        // dotnet.exe's DPI context can return virtualized bounds while PrintWindow paints physical pixels.
        var assembly = typeof(NativeWorkerClient).Assembly.Location;
        var apphost = Path.ChangeExtension(assembly, ".exe");
        if (!File.Exists(apphost)) throw new FileNotFoundException("DPI-aware native worker apphost is required.", apphost);
        var start = new ProcessStartInfo(apphost) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Native worker could not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr); token.ThrowIfCancellationRequested(); return false;
        }
        await Task.WhenAll(stdout, stderr);
        return process.ExitCode == 0;
    }
}
