using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace QNAgent;

internal sealed partial class MainWindow
{
    private sealed record LogLevelChoice(DiagnosticLogLevel Value, string Label) { public override string ToString() => Label; }
    private sealed record LogPeriodChoice(int Hours, string Label) { public override string ToString() => Label; }
    private readonly ComboBox _logLevel = new() { MinWidth = 240 };
    private readonly ComboBox _logPeriod = new() { MinWidth = 240 };
    private readonly TextBlock _diagnosticStatus = Text("", 13);
    private bool _loadingLogLevel = true;
    private UIElement BuildDiagnostics()
    {
        var panel = Stack(); panel.Children.Add(Text("日志与诊断", 28));
        panel.Children.Add(Text("朋友测试遇到问题时，切换到“调试”，复现一次，再导出诊断包。平时使用“信息”即可。"));
        _logLevel.ItemsSource = new[]
        {
            new LogLevelChoice(DiagnosticLogLevel.Error, "错误"), new LogLevelChoice(DiagnosticLogLevel.Warning, "警告"),
            new LogLevelChoice(DiagnosticLogLevel.Information, "信息（推荐）"), new LogLevelChoice(DiagnosticLogLevel.Debug, "调试")
        };
        var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _logLevel.Height = 38; _logLevel.VerticalAlignment = VerticalAlignment.Center; _logLevel.Margin = new Thickness(0, 0, 12, 0); row.Children.Add(_logLevel);
        var open = Button("打开日志目录", () =>
        {
            Directory.CreateDirectory(_store.Diagnostics.DirectoryPath);
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true }; start.ArgumentList.Add(_store.Diagnostics.DirectoryPath); Process.Start(start);
        });
        Grid.SetColumn(open, 1); row.Children.Add(open);
        Field(panel, "日志级别（立即生效并记住设置）", row);
        panel.Children.Add(Text("级别包含更严重的事件。日志按大小轮换，最多 10 个文件、单段约 2 MB，并清理超过 7 天的日志。", 12));
        _logPeriod.ItemsSource = new[] { new LogPeriodChoice(24, "最近 24 小时"), new LogPeriodChoice(168, "最近 7 天"), new LogPeriodChoice(0, "全部已保留记录") };
        _logPeriod.SelectedIndex = 0; Field(panel, "导出范围", _logPeriod);
        panel.Children.Add(Button("导出诊断包（ZIP）", ExportDiagnosticsAsync));
        panel.Children.Add(Text("导出包含：程序构建指纹、系统 / .NET 版本、结构化错误、调用状态和耗时、上下文条数、脱敏关联标识。", 13));
        panel.Children.Add(Text("不包含：API Key（含加密值）、聊天正文、模型回复、截图、买家 / 店铺名称、完整网址、商品资料或数据库。不会自动上传或发送。", 13));
        panel.Children.Add(Text("日志级别只控制之后的新记录，未记录的调试信息不能补回。诊断包也会提取已有调用记录的技术摘要。", 12));
        panel.Children.Add(_diagnosticStatus);
        _logLevel.SelectionChanged += (_, _) =>
        {
            if (_loadingLogLevel || _logLevel.SelectedItem is not LogLevelChoice choice) return;
            try
            {
                _settings = _store.Settings() with { LogLevel = choice.Value }; _store.SaveSettings(_settings);
                _store.Diagnostics.Write(DiagnosticLogLevel.Information, DiagnosticEvent.LogLevelChanged);
                RefreshDiagnosticStatus();
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            { _store.Diagnostics.Write(DiagnosticLogLevel.Error, DiagnosticEvent.UiOperationFailed, error: error); _diagnosticStatus.Text = "日志级别保存失败，请检查数据目录是否可写。"; }
        };
        return Scroll(panel);
    }
    private void LoadDiagnosticLevel()
    {
        _loadingLogLevel = true;
        _logLevel.SelectedItem = _logLevel.Items.Cast<LogLevelChoice>().FirstOrDefault(i => i.Value == _settings.LogLevel) ?? _logLevel.Items[2];
        _loadingLogLevel = false; RefreshDiagnosticStatus();
    }
    private void RefreshDiagnosticStatus() => _diagnosticStatus.Text = _store.Diagnostics.LastWriteError is { } error
        ? "日志写入异常（" + error + "），请检查目录权限。"
        : "当前日志目录：" + _store.Diagnostics.DirectoryPath + "\n导出后可把 ZIP 文件交给测试负责人，并说明出错时间和操作步骤。";
    private async Task ExportDiagnosticsAsync()
    {
        var choice = _logPeriod.SelectedItem as LogPeriodChoice ?? new(24, "最近24小时");
        var dialog = new SaveFileDialog { Filter = "诊断压缩包|*.zip", DefaultExt = ".zip", FileName = "QNAgent-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip" };
        if (dialog.ShowDialog(this) != true) return;
        var since = choice.Hours == 0 ? DateTimeOffset.MinValue : DateTimeOffset.UtcNow.AddHours(-choice.Hours);
        _diagnosticStatus.Text = "正在整理技术日志，请稍候…";
        var result = await Task.Run(() => DiagnosticExporter.Export(_store, dialog.FileName, since, _lifetime.Token), _lifetime.Token);
        _diagnosticStatus.Text = $"已导出 {result.Events} 条事件、{result.Calls} 条调用摘要。\n{result.Path}" +
            (result.Skipped > 0 ? $"\n另有 {result.Skipped} 条超限或无法读取的记录未包含。" : "");
    }
}
