using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using QnocrPoc.Models;

namespace QNAgent;

public sealed record WindowBinding(string ScopeId, string RuntimeKey, string SellerId, string WindowTitle);

internal sealed class WindowBindingRegistry(ClientStore shared)
{
    public static string RuntimeKey(QianniuWindow window)
    {
        using var process = Process.GetProcessById(window.Pid);
        return $"{window.Pid}:{window.Hwnd:X}:{process.StartTime.ToUniversalTime().Ticks}";
    }
    public WindowBinding GetOrCreate(QianniuWindow window, string sellerId, string? runtimeKey = null)
    {
        sellerId = sellerId.Trim();
        if (sellerId.Length is 0 or > 128 || sellerId.Any(char.IsControl)) throw new InvalidDataException("请填写有效的店铺标识。");
        sellerId = shared.Business.Resolve(sellerId).BindingKey;
        var key = runtimeKey ?? RuntimeKey(window);
        var bindings = shared.ReadValue<List<WindowBinding>>("window-bindings.json") ?? new();
        var existing = bindings.SingleOrDefault(b => b.RuntimeKey == key && b.SellerId == sellerId && b.WindowTitle == window.WindowTitle);
        if (existing is not null) { OpenStore(existing); return existing; }
        var binding = new WindowBinding(Guid.NewGuid().ToString("N"), key, sellerId, window.WindowTitle);
        bindings.Add(binding); shared.Write("window-bindings.json", bindings); return binding;
    }
    public ClientStore OpenStore(WindowBinding binding)
    {
        if (!Guid.TryParseExact(binding.ScopeId, "N", out _)) throw new InvalidDataException("窗口数据标识无效。");
        return new ClientStore(Path.Combine(shared.Root, "windows", binding.ScopeId), shared.DiagnosticRoot);
    }
}
