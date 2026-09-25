using System.Diagnostics;
using System.Text.Json;

namespace QnocrPoc.Shadow;

public sealed record ShadowAdapterResult(string Status, int ConvertedMessages, string? ReceiptPath, string? Reason);

/// <summary>Calls only the isolated conversion-only host. Never attaches to a running Bot process.</summary>
public sealed class ShadowAdapterClient
{
    public async Task<ShadowAdapterResult> ConvertAsync(string envelopePath, string? identityPath, string outputDirectory, CancellationToken token)
    {
        var host = Path.GetFullPath(Path.Combine("Validation", "QnbotCompatibility", "bin", "Debug", "net48", "QnbotCompatibility.exe"));
        if (!File.Exists(host)) return new("HostUnavailable", 0, null, "Build the pinned compatibility host first; no runtime Bot fallback.");
        Directory.CreateDirectory(outputDirectory);
        var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--shadow-convert");
        start.ArgumentList.Add(Path.GetFullPath(envelopePath));
        start.ArgumentList.Add(identityPath is null ? "-" : Path.GetFullPath(identityPath));
        start.ArgumentList.Add(Path.GetFullPath(outputDirectory));
        using var process = Process.Start(start) ?? throw new IOException("Could not start isolated adapter host.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            token.ThrowIfCancellationRequested();
            return new("HostTimeout", 0, null, "Only the task-owned adapter helper was terminated after 20 seconds.");
        }
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "adapter-process.log"), await stdout + "\n" + await stderr, token);
        var receiptPath = Path.Combine(outputDirectory, "shadow-adapter-result.json");
        if (process.ExitCode != 0 || !File.Exists(receiptPath)) return new("HostFailed", 0, null, $"Isolated adapter exit code {process.ExitCode}; see adapter-process.log.");
        using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(receiptPath, token));
        var value = receipt.RootElement;
        if (value.GetProperty("Mode").GetString() != "ShadowConversionOnly" || value.GetProperty("CoordinatorCreated").GetBoolean() ||
            value.GetProperty("ForbiddenBusinessCalls").GetInt32() != 0 || value.GetProperty("RuntimeInitializers").GetInt32() != 0)
            throw new InvalidDataException("Adapter receipt violates conversion-only boundary.");
        return new(value.GetProperty("Status").GetString()!, value.GetProperty("ConvertedMessages").GetInt32(), Path.GetFullPath(receiptPath),
            value.GetProperty("Reason").GetString());
    }
}
