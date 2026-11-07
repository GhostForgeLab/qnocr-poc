using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace QNAgent;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        DiagnosticLog? diagnostics = null;
        try
        {
#if PORTABLE_RELEASE
            // A distributed copy must never discover the developer's data via a parent directory.
            var root = AppContext.BaseDirectory;
            Directory.SetCurrentDirectory(root);
            var data = Path.Combine(root, "Data");
            if (args.Length == 1 && args[0] == "--portable-checks") return PortableSmoke.Run();
            if (args.Length != 0 && !(args.Length == 2 && args[0] == "--export-diagnostics"))
                throw new ArgumentException("测试包仅支持正常启动、--portable-checks 和 --export-diagnostics。");
#else
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "qnocr-poc.csproj"))) dir = dir.Parent;
            var root = dir?.FullName ?? AppContext.BaseDirectory;
            Directory.SetCurrentDirectory(root);
            var data = args.Length == 2 && args[0] == "--data-dir" ? Path.GetFullPath(args[1]) : Path.Combine(root, "Output", "DesktopClient");
#endif
            if (args.Length == 2 && args[0] == "--export-diagnostics")
            {
                var exported = DiagnosticExporter.Export(new ClientStore(data), args[1], DateTimeOffset.UtcNow.AddDays(-7));
                Console.WriteLine($"Exported {exported.Events} events and {exported.Calls} call summaries: {exported.Path}"); return 0;
            }
#if !PORTABLE_RELEASE
            if (args.Length == 1 && args[0] == "--business-learning-checks") return BusinessLearningChecks.Run();
            if (args.Length == 1 && args[0] == "--learning-transport-checks") return LearningTransportChecks.Run();
            if (args.Length == 1 && args[0] == "--learning-live-checks") return LearningTransportChecks.LiveAsync(root).GetAwaiter().GetResult();
            if (args.Length == 2 && args[0] == "--recognition-image-check") return RecognitionImageChecks.ReplayAsync(args[1]).GetAwaiter().GetResult();
            if (args.Length == 1 && args[0] == "--self-test") return ClientChecks.Run(root).GetAwaiter().GetResult();
            if (args.Length == 1 && args[0] == "--knowledge-editor-checks") return KnowledgeEditorChecks.Run();
            if (args.Length == 1 && args[0] == "--clipboard-checks") return ClipboardChecks.Run();
            if (args.Length == 1 && args[0] == "--jev-checks") return JevPipelineChecks.RunAsync().GetAwaiter().GetResult();
            if (args.Length == 1 && args[0] == "--jev-live-checks") return JevPipelineChecks.LiveAsync().GetAwaiter().GetResult();
            if (args.Length == 1 && args[0] == "--jev-reply-live-checks") return JevPipelineChecks.LiveReplyAsync(root).GetAwaiter().GetResult();
            if (args.Length == 1 && args[0] == "--auto-reply-checks") return AutoReplyChecks.Run();
            if (args.Length == 1 && args[0] == "--real-catalog-replay") return RealCatalogReplayChecks.RunAsync(root).GetAwaiter().GetResult();
            if (args.Length == 1 && args[0] == "--reply-transport-checks") return ReplyTransportChecks.RunAsync().GetAwaiter().GetResult();
            if (args.Length == 1 && args[0] == "--real-comparison-replay") return RealCatalogReplayChecks.RunAsync(root, comparisonOnly: true).GetAwaiter().GetResult();
            if (args.Length == 1 && args[0] == "--session-recovery-checks") return SessionRecoveryChecks.Run();
            if (args.Length == 1 && args[0] == "--knowledge-advice-checks") return KnowledgeAdviceChecks.Run();
            if (args.Length == 1 && args[0] == "--intent-card-checks") return IntentCardChecks.Run();
            if (args.Length == 1 && args[0] == "--dialogue-checks") return DialogueChecks.Run();
            if (args.Length == 1 && args[0] == "--diagnostic-checks") return DiagnosticChecks.Run();
            if (args.Length == 1 && args[0] == "--dialogue-live-capture") return DialogueChecks.CaptureLiveAsync().GetAwaiter().GetResult();
            if (args.Length == 1 && args[0] == "--dialogue-native-checks") return DialogueChecks.NativeOcrAsync().GetAwaiter().GetResult();
            if (args.Length == 1 && args[0] == "--intent-purpose-live-checks") return IntentCardChecks.LiveAsync(root).GetAwaiter().GetResult();
            if (args.Length == 1 && args[0] == "--knowledge-advice-http-checks") return KnowledgeAdviceChecks.HttpAsync().GetAwaiter().GetResult();
            if (args.Length == 1 && args[0] == "--knowledge-advice-live-checks") return KnowledgeAdviceChecks.LiveAsync(root).GetAwaiter().GetResult();
            if (args.Length == 1 && args[0] == "--saved-session-replay") return SessionRecoveryChecks.ReplaySavedWindows(root);
            if (args.Length == 1 && args[0] == "--reply-options-live-checks") return JevPipelineChecks.LiveOptionsAsync(root).GetAwaiter().GetResult();
            if (args.Length == 1 && args[0] == "--configure-jev-from-env")
            {
                var key = Environment.GetEnvironmentVariable("TYPESAFE_API_KEY");
                if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("TYPESAFE_API_KEY 未配置。");
                var store = new ClientStore(data); var settings = store.Settings();
                store.SaveSettings(settings with { JevSecret = SecretVault.Protect(key) });
                Console.WriteLine("JEV key saved using current Windows user encryption; other settings preserved."); return 0;
            }
            if (args.Length == 1 && args[0] == "--dock-checks") return DockChecks.Run();
            if (args.Length == 1 && args[0] == "--multi-window-checks") return MultiWindowChecks.Run();
            if (args.Length == 1 && args[0] is "--switch-checks" or "--switch-live-checks")
                return SwitchLatencyChecks.Run(args[0] == "--switch-live-checks").GetAwaiter().GetResult();
            if (args.Length == 1 && args[0] is "--memory-capture-checks" or "--memory-capture-live-checks")
                return MemoryCaptureChecks.Run(args[0] == "--memory-capture-live-checks").GetAwaiter().GetResult();
#endif
            using var mutex = new Mutex(true, "Local\\QNAgentDesktop-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(data)))[..16], out var created);
            if (!created) { MessageBox.Show("QN Agent 已经打开。请查看千牛旁的会话小窗，或双击任务栏右下角的助手托盘图标打开设置。", "QN Agent"); return 0; }
            var runtimeStore = new ClientStore(data); diagnostics = runtimeStore.Diagnostics;
            diagnostics.Configure(runtimeStore.Settings().LogLevel);
            diagnostics.Write(DiagnosticLogLevel.Information, DiagnosticEvent.AppStarted);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => diagnostics.Write(DiagnosticLogLevel.Error, DiagnosticEvent.UnhandledError, error: e.ExceptionObject as Exception);
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) => diagnostics.Write(DiagnosticLogLevel.Error, DiagnosticEvent.UnhandledError, error: e.Exception);
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.DispatcherUnhandledException += (_, eventArgs) =>
            {
                diagnostics.Write(DiagnosticLogLevel.Error, DiagnosticEvent.UnhandledError, error: eventArgs.Exception);
                MessageBox.Show("操作未完成：" + eventArgs.Exception.Message, "QN Agent");
                eventArgs.Handled = true;
            };
            app.Exit += (_, _) => diagnostics.Write(DiagnosticLogLevel.Information, DiagnosticEvent.AppStopped);
            return app.Run(new MainWindow(runtimeStore));
        }
        catch (Exception error)
        {
            if (args.Any(a => a is "--business-learning-checks" or "--learning-transport-checks" or "--learning-live-checks")) { Console.Error.WriteLine(error); return 1; }
            diagnostics?.Write(DiagnosticLogLevel.Error, DiagnosticEvent.UnhandledError, error: error);
            if (args.Contains("--portable-checks")) { Console.Error.WriteLine(error); return 1; }
            if (args.Any(arg => arg is "--recognition-image-check" or "--export-diagnostics" or "--diagnostic-checks" or "--dialogue-native-checks" or "--dialogue-live-capture" or "--dialogue-checks" or "--intent-card-checks" or "--intent-purpose-live-checks" or "--knowledge-advice-checks" or "--knowledge-advice-http-checks" or "--knowledge-advice-live-checks" or "--session-recovery-checks" or "--saved-session-replay" or "--real-comparison-replay" or "--reply-transport-checks" or "--self-test" or "--real-catalog-replay" or "--auto-reply-checks" or "--reply-options-live-checks" or "--configure-jev-from-env" or "--jev-checks" or "--jev-live-checks" or "--jev-reply-live-checks" or "--clipboard-checks" or "--knowledge-editor-checks" or "--dock-checks" or "--multi-window-checks" or "--switch-checks" or "--switch-live-checks" || arg.StartsWith("--memory-capture-", StringComparison.Ordinal))) { Console.Error.WriteLine(error); return 1; }
            MessageBox.Show("启动失败：" + error.Message, "QN Agent"); return 1;
        }
    }
}
