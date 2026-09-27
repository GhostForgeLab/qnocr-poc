using System.Drawing;
using System.Drawing.Imaging;
using QnocrPoc.Detection;

if (args.Length == 2 && args[0] == "--product-provider-live-smoke")
    return await ProductProviderLiveSmoke.RunAsync(args[1]);

if (args.Length == 2 && args[0] == "--provider-live-smoke")
    return await ProviderLiveSmoke.RunAsync(args[1]);

var root = Path.GetFullPath(Path.Combine("Output", "ValidationV04"));
Directory.CreateDirectory(root);
var passed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL: " + message);
    passed++;
    Console.WriteLine("PASS: " + message);
}
void Throws<T>(Action action, string message) where T : Exception
{
    try { action(); }
    catch (T) { Check(true, message); return; }
    throw new Exception("FAIL: " + message);
}
string Frame(string name, int width = 200, int height = 100, Rectangle? patch = null, Color? color = null)
{
    var path = Path.Combine(root, name + ".png");
    using var bitmap = new Bitmap(width, height);
    using (var graphics = Graphics.FromImage(bitmap))
    {
        graphics.Clear(Color.White);
        if (patch is { } area)
        {
            using var brush = new SolidBrush(color ?? Color.Black);
            graphics.FillRectangle(brush, area);
        }
    }
    bitmap.Save(path, ImageFormat.Png);
    return path;
}

if (args.Length == 1 && args[0] == "--assistant-operations-only")
{
    AssistantOperationsChecks.Run(Check);
EvaluationBatchChecks.Run(Check);
    Console.WriteLine($"Passed {passed} assistant operations checks.");
    return 0;
}

var detector = new ChatRegionChangeDetector();
var baseline = Frame("baseline");
var identical = detector.Detect(baseline, baseline);
Check(!identical.Changed && identical.DifferenceRatio == 0 && identical.ChangedRectangle is null, "identical frames: no change, zero ratio, null rectangle");
var patch = new Rectangle(20, 30, 10, 10);
var changed = Frame("patch", patch: patch);
var diff = detector.Detect(baseline, changed);
Check(diff.Changed && diff.DifferentPixelCount == 100 && Math.Abs(diff.DifferenceRatio - 0.005) < 1e-12 && diff.ChangedRectangle == patch,
    "known 100-pixel patch: ratio 0.005 and exact rectangle");
Check(!detector.Detect(baseline, Frame("threshold24", patch: patch, color: Color.FromArgb(231, 255, 255))).Changed,
    "channel difference exactly 24 is ignored");
Check(detector.Detect(baseline, Frame("threshold25", patch: patch, color: Color.FromArgb(230, 255, 255))).Changed,
    "single-channel difference 25 is detected");
Check(!detector.Detect(baseline, Frame("tiny", patch: new Rectangle(5, 5, 1, 1))).Changed, "one-pixel noise is ignored");
Check(detector.Detect(baseline, Frame("ratio_boundary", patch: new Rectangle(5, 5, 5, 4))).Changed,
    "exactly 20 pixels and ratio 0.001 meet both inclusive minimum thresholds");
Check(detector.Detect(baseline, Frame("whole_frame", patch: new Rectangle(0, 0, 200, 100))).ChangedRectangle == new Rectangle(0, 0, 200, 100),
    "global changes are reported as full-frame changes, not silently discarded");
Check(!detector.Detect(Frame("large_base", 800, 500), Frame("small_ratio", 800, 500, patch)).Changed,
    "100 pixels below minimum ratio are ignored");
var edge = new Rectangle(190, 90, 10, 10);
var edgePath = Frame("edge", patch: edge);
Check(detector.Detect(baseline, edgePath).ChangedRectangle == edge, "right/bottom edge is included without off-by-one loss");
Throws<ArgumentException>(() => detector.Detect(baseline, Frame("wrong_size", 100, 100)), "different frame sizes are rejected");
Throws<FileNotFoundException>(() => detector.Detect(Path.Combine(root, "missing.png"), baseline), "missing frame is rejected");
var invalid = Path.Combine(root, "invalid.png");
File.WriteAllText(invalid, "not an image");
Throws<InvalidDataException>(() => detector.Detect(invalid, baseline), "invalid image is rejected");
Throws<ArgumentOutOfRangeException>(() => new ChatRegionChangeDetector(minimumDifferenceRatio: double.NaN), "NaN threshold is rejected");
Throws<ArgumentOutOfRangeException>(() => new ChatRegionChangeDetector(minimumChangedPixels: 0), "zero minimum pixel count is rejected");

var calls = 0;
var pipeline = new ChatRegionChangePipeline();
var pipelineOutput = Path.Combine(root, "pipeline_" + Guid.NewGuid().ToString("N"));
pipeline.Process(baseline, baseline, pipelineOutput, _ => calls++, _ => { });
Check(calls == 0 && !Directory.Exists(pipelineOutput), "unchanged frames do not create change artifacts or call OCR");
pipeline.Process(baseline, edgePath, pipelineOutput, path =>
{
    calls++;
    using var crop = new Bitmap(path);
    Check(crop.Width == 200 && crop.Height == 22 && crop.GetPixel(199, 21).ToArgb() == Color.Black.ToArgb(),
        "OCR context band is clipped to the image and preserves changed edge pixels");
}, _ => { });
pipeline.Process(edgePath, edgePath, pipelineOutput, _ => calls++, _ => { });
Check(calls == 1, "A -> B -> B calls OCR once (callback test)");
var failedOutput = Path.Combine(root, "failed_" + Guid.NewGuid().ToString("N"));
Throws<InvalidOperationException>(() => pipeline.Process(baseline, changed, failedOutput,
    _ => throw new InvalidOperationException("simulated OCR failure"), _ => { }), "OCR failure propagates to the caller");
Check(File.ReadAllText(Directory.GetFiles(failedOutput, "change.json", SearchOption.AllDirectories).Single()).Contains("OcrFailed"),
    "OCR failure retains artifacts and records failed status");

// Synthetic message arrival: same old line, with one new line in the second image.
foreach (var isCurrent in new[] { false, true })
{
    using var bitmap = new Bitmap(654, 537);
    using (var graphics = Graphics.FromImage(bitmap))
    using (var font = new Font("Microsoft YaHei", 20, FontStyle.Regular, GraphicsUnit.Pixel))
    {
        graphics.Clear(Color.White);
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        graphics.DrawString("历史消息: 昨天已发货", font, Brushes.Black, 24, 40);
        if (isCurrent) graphics.DrawString("新消息: TA1 200ml 304 F2U 还有货吗?", font, Brushes.Black, 24, 180);
    }
    bitmap.Save(Path.Combine(root, isCurrent ? "current_message.png" : "previous_message.png"), ImageFormat.Png);
}
var message = detector.Detect(Path.Combine(root, "previous_message.png"), Path.Combine(root, "current_message.png"));
Check(message.Changed && message.ChangedRectangle is { Top: > 160, Bottom: < 230 }, "new message fixture change excludes the old message");
await LayoutChecks.RunAsync(Check);
await ResolverChecks.RunAsync(Check);
ProductChecks.Run(Check);
MessageChecks.Run(Check);
await AdapterChecks.RunAsync(Check);
await ShadowChecks.RunAsync(Check);
await AiShadowChecks.RunAsync(Check);
await StabilizationChecks.RunAsync(Check);
BindingChecks.Run(Check);
await AgentPipelineChecks.RunAsync(Check);
await DecisionChecks.RunAsync(Check);
await KnowledgeChecks.RunAsync(Check);
await ProductKnowledgeChecks.RunAsync(Check);
await MultiProductKnowledgeChecks.RunAsync(Check);
await ProductKnowledgeSchemaChecks.RunAsync(Check);
await KnowledgeWorkflowChecks.RunAsync(Check);
await KnowledgeAuthoringChecks.RunAsync(Check);
await KnowledgeImportRevisionChecks.RunAsync(Check);
await KnowledgeConsoleChecks.RunAsync(Check);
await KnowledgeSourceAdapterChecks.RunAsync(Check);
FeedbackRouterChecks.Run(Check);
await IdentityChecks.RunAsync(Check);
await ConfirmationChecks.RunAsync(Check);
await StabilityChecks.RunAsync(Check);
AssistantChecks.Run(Check);
AssistantOperationsChecks.Run(Check);
EvaluationBatchChecks.Run(Check);
Console.WriteLine($"Passed {passed} checks (V0.4 through V1.25). Fixtures: {root}");
return 0;
