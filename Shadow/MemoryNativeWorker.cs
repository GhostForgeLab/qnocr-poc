using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using QnocrPoc.Capture;
using QnocrPoc.Models;
using QnocrPoc.OCR;

namespace QnocrPoc.Shadow;

public sealed record MemoryWorkerRequest(string Operation, long Hwnd = 0, int Pid = 0,
    string Title = "", byte[]? Png = null, string SourceId = "memory:frame");
public sealed record MemoryWorkerResponse(bool Success, string? Error = null, bool Waiting = false,
    byte[]? Png = null, OcrResult? Ocr = null, bool Unchanged = false);

/// <summary>Sequential, bounded IPC. No screenshots or OCR payloads are written to disk/stdout.</summary>
public sealed class MemoryNativeWorker : IAsyncDisposable
{
    // Cap concurrent heavy OCR across all window connections; capture and cancellation stay independent.
    private static readonly SemaphoreSlim OcrSlots = new(2, 2);
    private readonly NamedPipeServerStream _pipe;
    private Process? _process;
    private Task? _drains;
    private bool _disposed;
    public int ProcessId => _process?.Id ?? throw new InvalidOperationException("Worker has not started.");

    private MemoryNativeWorker(string pipeName)
    {
        _pipe = new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    public static async Task<MemoryNativeWorker> StartAsync(CancellationToken token, int cpuThreads = 1)
    {
        var name = "qnagent-frames-" + Guid.NewGuid().ToString("N");
        var worker = new MemoryNativeWorker(name);
        cpuThreads = Math.Clamp(cpuThreads, 1, 2);
        try
        {
            var host = Path.ChangeExtension(typeof(MemoryNativeWorker).Assembly.Location, ".exe");
            var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--memory-worker"); start.ArgumentList.Add(name);
            start.Environment["QNAGENT_OCR_THREADS"] = cpuThreads.ToString();
            start.Environment["OMP_NUM_THREADS"] = cpuThreads.ToString();
            start.Environment["MKL_NUM_THREADS"] = cpuThreads.ToString();
            start.Environment["OPENBLAS_NUM_THREADS"] = "1";
            start.Environment["OMP_WAIT_POLICY"] = "PASSIVE";
            start.Environment["KMP_BLOCKTIME"] = "0";
            worker._process = Process.Start(start) ?? throw new IOException("无法启动内存采集进程。");
            // Native libraries may write diagnostics; drain without retaining unbounded strings.
            worker._drains = Task.WhenAll(worker._process.StandardOutput.BaseStream.CopyToAsync(Stream.Null),
                worker._process.StandardError.BaseStream.CopyToAsync(Stream.Null));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await worker._pipe.WaitForConnectionAsync(timeout.Token);
            return worker;
        }
        catch { await worker.DisposeAsync(); throw; }
    }

    public Task<MemoryWorkerResponse> CaptureAsync(QianniuWindow window, CancellationToken token) =>
        RequestAsync(new("capture", (long)window.Hwnd, window.Pid, window.WindowTitle), TimeSpan.FromSeconds(20), token);

    public async Task<OcrResult> RecognizeAsync(byte[] png, string sourceId, CancellationToken token)
    {
        var result = await RequestOcrAsync(new("ocr", Png: png, SourceId: sourceId), token);
        return result.Ocr ?? throw new IOException("识别进程没有返回有效结果。");
    }
    public async Task<OcrResult> RecognizeChatAsync(byte[] png, string sourceId, CancellationToken token)
    {
        var result = await RequestOcrAsync(new("ocr-chat", Png: png, SourceId: sourceId), token);
        return result.Ocr ?? throw new IOException("聊天识别没有返回有效结果。");
    }
    private async Task<MemoryWorkerResponse> RequestOcrAsync(MemoryWorkerRequest request, CancellationToken token)
    {
        await OcrSlots.WaitAsync(token);
        try { return await RequestAsync(request, TimeSpan.FromSeconds(45), token); }
        finally { OcrSlots.Release(); }
    }

    private async Task<MemoryWorkerResponse> RequestAsync(MemoryWorkerRequest request, TimeSpan limit, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(limit);
        try
        {
            await MemoryWorkerWire.WriteAsync(_pipe, request, deadline.Token);
            var result = await MemoryWorkerWire.ReadAsync<MemoryWorkerResponse>(_pipe, deadline.Token);
            if (!result.Success) throw new IOException(result.Error ?? "内存采集进程失败。");
            return result;
        }
        catch (OperationCanceledException)
        {
            await DisposeAsync();
            token.ThrowIfCancellationRequested();
            throw new TimeoutException("采集或识别超时，工作进程已停止，请重新连接。");
        }
        catch { await DisposeAsync(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _pipe.Dispose();
        if (_process is not null)
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
            if (_drains is not null) await _drains;
            _process.Dispose();
        }
    }

    public static async Task<int> RunAsync(string name)
    {
        using var process = Process.GetCurrentProcess();
        process.PriorityClass = ProcessPriorityClass.BelowNormal;
        OpenCvSharp.Cv2.SetNumThreads(1);
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10000);
        var cpuThreads = Environment.GetEnvironmentVariable("QNAGENT_OCR_THREADS") == "2" ? 2 : 1;
        using var ocr = new PaddleOcrService(cpuThreads);
        string? previousFingerprint = null;
        try
        {
            while (pipe.IsConnected)
            {
                var request = await MemoryWorkerWire.ReadAsync<MemoryWorkerRequest>(pipe, CancellationToken.None);
                MemoryWorkerResponse response;
                try
                {
                    if (request.Operation == "capture")
                    {
                        var window = new QianniuWindowCapture().ReadWindow((nint)request.Hwnd, request.Pid, request.Title);
                        if (window is null) throw new IOException("接待窗口已关闭或店铺窗口已变化，请重新连接。");
                        if (!window.IsVisible || window.IsMinimized || window.IsCloaked)
                            response = new(true, Waiting: true);
                        else
                        {
                            var capture = QianniuWindowCapture.CaptureMemory(window, previousFingerprint, out var fingerprint);
                            response = new(capture.Result.Success, capture.Result.Success ? null : "千牛画面为空或采集失败，请恢复接待窗口。",
                                Png: capture.Png, Unchanged: capture.Result.Success && capture.Png is null);
                            if (capture.Result.Success) previousFingerprint = fingerprint;
                        }
                    }
                    else if (request.Operation is "ocr" or "ocr-chat" && request.Png is { Length: > 0 })
                        response = new(true, Ocr: ocr.Recognize(request.Png, request.SourceId, request.Operation == "ocr-chat"));
                    else throw new InvalidDataException("无效的内存采集请求。");
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                { response = new(false, error.Message); }
                await MemoryWorkerWire.WriteAsync(pipe, response, CancellationToken.None);
            }
        }
        catch (IOException) { /* parent disconnected */ }
        return 0;
    }
}

public static class MemoryWorkerWire
{
    private const int MaximumPacketBytes = 64 * 1024 * 1024;
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MaximumPacketBytes) throw new InvalidDataException("内存画面超过传输上限。");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(bytes, token); await stream.FlushAsync(token);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaximumPacketBytes) throw new InvalidDataException("无效的内存画面长度。");
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("内存画面响应为空。");
    }
}
