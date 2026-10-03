using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using QnocrPoc.Adapter;
using QnocrPoc.Capture;
using QnocrPoc.Extension.AgentContext;
using QnocrPoc.Layout;
using QnocrPoc.Messages;
using QnocrPoc.Models;
using QnocrPoc.Shadow;

// Validation-only host. No AI/Intent/Knowledge/Reply/send dependency or dispatch.
internal static class RealMultiBuyerContextChecks
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly string[] Stages = ["A1A2", "B1B2", "A3", "A4", "B3"];
    private static readonly string[] SingleStages = ["A1A2", "A4"];
    internal sealed record Buyers(string BuyerA, string BuyerB);
    internal sealed record Receipt(string Stage, string Buyer, string Frame, MessageEnvelope Envelope, AgentSession Context);
    internal sealed record Alias(string Buyer, string Raw, string Normalized);
    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? throw new InvalidDataException("Missing evidence.");
    private static void Write<T>(string path, T value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
    private static void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private static string Observed(string role, string label) => "observed:" + role + ":" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(label))).ToLowerInvariant();
    private static string Snapshot(IEnumerable<AgentSession> sessions) => JsonSerializer.Serialize(sessions.OrderBy(s => s.SessionId, StringComparer.Ordinal), Json);
    private static bool Equal(AgentSession? a, AgentSession b) => a is not null && a.SessionId == b.SessionId &&
        a.Identity == b.Identity && a.Status == b.Status && a.CreatedTime == b.CreatedTime && a.LastActiveTime == b.LastActiveTime &&
        a.Messages.SequenceEqual(b.Messages) && a.Messages.Select(m => m.Timestamp.Kind).SequenceEqual(b.Messages.Select(m => m.Timestamp.Kind)) &&
        a.StateHistory.SequenceEqual(b.StateHistory);
    private static Dictionary<string, string[]> Messages(string prefix) => new(StringComparer.Ordinal)
    {
        ["A1A2"] = [prefix + "01规格测试", prefix + "02容量测试"],
        ["B1B2"] = [prefix + "11颜色测试", prefix + "12发货测试"],
        ["A3"] = [prefix + "03切换测试"], ["A4"] = [prefix + "04恢复测试"], ["B3"] = [prefix + "13恢复测试"]
    };
    private static string BuyerFor(string stage) => stage.StartsWith('A') ? "A" : "B";

    private sealed class Run : IDisposable
    {
        private AgentContextPipeline _pipeline;
        private readonly TimeProvider? _clock;
        private readonly Dictionary<string, HashSet<string>> _accepted = new(StringComparer.Ordinal)
            { ["A"] = new(StringComparer.Ordinal), ["B"] = new(StringComparer.Ordinal) };
        private readonly Dictionary<string, string> _sessionIds = new(StringComparer.Ordinal);
        public Dictionary<string, string> BuyerIds { get; }
        public Dictionary<string, string[]> Texts { get; }
        public List<MessageEnvelope> Envelopes { get; } = [];
        public string Database { get; }
        public Run(string database, Buyers buyers, string prefix, TimeProvider? clock = null, bool single = false, Dictionary<string, string[]>? messagePlan = null, bool resume = false)
        {
            var normalizer = new IdentityNormalizer();
            var a = normalizer.Normalize(buyers.BuyerA);
            BuyerIds = new(StringComparer.Ordinal) { ["A"] = Observed("buyer", a) };
            if (!single)
            {
                var b = normalizer.Normalize(buyers.BuyerB);
                Require(a != b, "Test buyers collapse to one Observed candidate; stop, do not merge.");
                BuyerIds["B"] = Observed("buyer", b);
            }
            Texts = messagePlan ?? Messages(prefix); Database = Path.GetFullPath(database);
            Require(resume ? single && File.Exists(Database) : !File.Exists(Database),
                "Use a fresh test database, or explicitly resume existing single-buyer evidence.");
            _clock = clock; _pipeline = new(Database, clock);
            if (resume)
            {
                var restored = Sessions();
                Require(restored.Length == 1 && restored[0].Identity.BuyerId == BuyerIds["A"], "Resume identity/store mismatch.");
                _sessionIds["A"] = restored[0].SessionId;
                foreach (var message in restored[0].Messages) _accepted["A"].Add(message.Content);
            }
        }
        public AgentSession[] Sessions() => new SQLiteAgentSessionStore(Database).LoadSessions().ToArray();
        public AgentSession Accept(string stage, MessageEnvelope envelope)
        {
            var buyer = BuyerFor(stage);
            Require(envelope.BuyerId == BuyerIds[buyer], "OCR buyer differs from stage; stop before Context write.");
            Require(envelope.Messages.Count > 0 && envelope.Messages.All(m => m.Role == "buyer" &&
                (Texts[stage].Contains(m.Text, StringComparer.Ordinal) || _accepted[buyer].Contains(m.Text))),
                "Marked message belongs to another buyer/stage or has unreviewed OCR text.");
            var context = _pipeline.ProcessMessage(envelope);
            if (_sessionIds.TryGetValue(buyer, out var id)) Require(context.SessionId == id, "Unexplained additional Session; stop.");
            else _sessionIds[buyer] = context.SessionId;
            Require(_sessionIds.Values.Distinct(StringComparer.Ordinal).Count() == _sessionIds.Count, "A/B Session collision; stop.");
            foreach (var message in envelope.Messages) _accepted[buyer].Add(message.Text);
            Require(context.Messages.Count == _accepted[buyer].Count &&
                context.Messages.Select(m => m.Content).ToHashSet(StringComparer.Ordinal).SetEquals(_accepted[buyer]),
                "Duplicate OCR message identity or cross-buyer history; stop.");
            ValidateContext(envelope, context);
            Require(Equal(new SQLiteAgentSessionStore(Database).GetSession(context.SessionId), context), "SQLite differs from Context.");
            Envelopes.Add(envelope); return context;
        }
        public bool Complete(string stage) => Texts[stage].All(_accepted[BuyerFor(stage)].Contains);
        public void Replay()
        {
            var before = Sessions();
            foreach (var envelope in Envelopes)
            {
                var copy = JsonSerializer.Deserialize<MessageEnvelope>(JsonSerializer.Serialize(envelope))!;
                Require(Equal(_pipeline.ProcessMessage(copy), before.Single(s => s.Identity.BuyerId == copy.BuyerId)),
                    "Replay changed IDs/messages/LastActiveTime/state.");
            }
            Require(Snapshot(Sessions()) == Snapshot(before), "Replay mutated SQLite.");
        }
        public Alias[] ReplayAliases(Buyers buyers)
        {
            var before = Snapshot(Sessions()); var aliases = new List<Alias>(); var n = new IdentityNormalizer();
            foreach (var buyer in BuyerIds.Keys)
            {
                var label = buyer == "A" ? buyers.BuyerA : buyers.BuyerB;
                foreach (var raw in new[] { " " + label + " ", label + "!", label.Normalize(NormalizationForm.FormKC), label.Normalize(NormalizationForm.FormKD) })
                {
                    var normalized = n.Normalize(raw); var id = Observed("buyer", normalized);
                    Require(id == BuyerIds[buyer], "Controlled alias changed normalized buyer key.");
                    foreach (var envelope in Envelopes.Where(e => e.BuyerId == BuyerIds[buyer]))
                        _pipeline.ProcessMessage(envelope with { BuyerId = id });
                    aliases.Add(new(buyer, raw, normalized));
                }
            }
            Require(Snapshot(Sessions()) == before, "Controlled alias replay changed SQLite/LastActiveTime.");
            return aliases.ToArray();
        }
        public void Rebuild()
        {
            var before = Sessions(); _pipeline.Dispose(); _pipeline = new(Database, _clock);
            foreach (var session in before) Require(Equal(_pipeline.GetSession(session.SessionId), session), "Rebuild changed Session.");
            Require(Snapshot(Sessions()) == Snapshot(before), "Rebuild changed SQLite."); Replay();
        }
        public void Dispose() => _pipeline.Dispose();
    }
    private static void ValidateContext(MessageEnvelope envelope, AgentSession context)
    {
        Require(context.Identity == new SessionIdentity(envelope.Source, envelope.SellerId, envelope.BuyerId) &&
            context.Identity.IdentityLevel == IdentityLevel.Observed && context.CurrentProductContext is null &&
            context.IntentHistory.Count == 0, "Context scope/trust/business state changed.");
        var provider = new AgentMessageIdentityProvider();
        foreach (var message in envelope.Messages)
            Require(context.Messages.Count(m => m.MessageId == provider.GetMessageId(context.Identity, message) &&
                m.SessionId == context.SessionId && m.Content == message.Text && m.Timestamp == message.Timestamp &&
                m.Timestamp.Kind == message.Timestamp.Kind && m.Role == message.Role && m.Source == envelope.Source) == 1,
                "Stable Identity or delivered fields differ from Envelope.");
    }
    private static BuyerMessageBatch Select(BuyerMessageBatch batch, string prefix)
    {
        var indices = Enumerable.Range(0, batch.Messages.Count).Where(i => batch.Messages[i].Text.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        return batch with { Messages = indices.Select(i => batch.Messages[i]).ToArray(),
            Evidence = indices.Select((i, ordinal) => batch.Evidence.Single(e => e.MessageIndex == i) with { MessageIndex = ordinal }).ToArray() };
    }

    public static async Task<int> CaptureAsync(string buyersPath, string directory, string prefix, bool single = false, string? planPath = null)
    {
        directory = Path.GetFullPath(directory);
        Require(prefix.Length >= 6 && prefix.All(char.IsAsciiDigit), "Use a unique numeric prefix of at least six digits.");
        Require(!Directory.Exists(directory) && !File.Exists(directory), "Use a new evidence directory.");
        var buyers = single ? new Buyers("", "") : Read<Buyers>(buyersPath);
        var normalizer = new IdentityNormalizer();
        var labels = new Dictionary<string, string>();
        if (!single)
        {
            labels["A"] = normalizer.Normalize(buyers.BuyerA); labels["B"] = normalizer.Normalize(buyers.BuyerB);
            Require(labels["A"] != labels["B"], "Distinct buyers normalize identically; blocked before capture.");
        }
        var capture = new QianniuWindowCapture();
        var windows = capture.FindWindows(_ => { }).Where(w => w.IsVisible && !w.IsMinimized && !w.IsCloaked &&
            w.WindowTitle.Contains("接待中心", StringComparison.Ordinal)).ToArray();
        Require(windows.Length == 1, "Exactly one visible reception window is required.");
        var window = windows.Single(); var seller = Observed("seller", window.WindowTitle);
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "sessions.db");
        Run? run = single ? null : new Run(database, buyers, prefix);
        var stages = single ? SingleStages : Stages;
        var texts = planPath is null ? Messages(prefix) : Read<Dictionary<string, string[]>>(planPath);
        Require(planPath is null || single && texts.Keys.Order().SequenceEqual(SingleStages.Order()) &&
            texts["A1A2"].Length == 2 && texts["A4"].Length == 1 &&
            texts.Values.SelectMany(x => x).Distinct(StringComparer.Ordinal).Count() == 3 &&
            texts.Values.SelectMany(x => x).All(x => x.StartsWith(prefix, StringComparison.Ordinal)),
            "Single buyer plan must contain exactly three distinct marked real message texts.");
        var startedAt = DateTimeOffset.UtcNow;
        void Metadata() => Write(Path.Combine(directory, "session.json"), new
        {
            Version = "1.34.0-C", Mode = "ShadowContextOnly",
            EvidenceKind = single ? "LiveQianniuSingleBuyerOCR" : "LiveQianniuOCR",
            AcceptanceScope = single ? "SingleBuyerReal" : "MultiBuyerReal",
            MultiBuyerRealStatus = single ? "Pending" : "UnderTest",
            StartedAt = startedAt, Prefix = prefix, DatabasePath = database, ExpectedMessages = stages.ToDictionary(stage => stage, stage => texts[stage]),
            WindowHandle = $"{window.Hwnd:X}", window.Pid, window.WindowTitle, SellerId = seller, Buyers = buyers,
            LabelBinding = single ? "FirstControlledMarkedBuyerMessageOCR" : "OperatorConfiguredOCRLabel",
            IdentityLevel = "Observed", CounterBasis = "No business dependencies or dispatch/send entrypoints in this validation path"
        });
        Metadata(); Write(Path.Combine(directory, "instructions.json"), stages.ToDictionary(stage => stage, stage => texts[stage]));
        var receipts = new List<Receipt>(); var status = "Incomplete"; string? failure = null; var frame = 0;
        try
        {
            foreach (var stage in stages)
            {
                if (stage == "A4")
                {
                    Require(run is not null, "Initial controlled messages must be captured before rebuild.");
                    Write(Path.Combine(directory, "before-restart.json"), run!.Sessions());
                    run!.Replay(); Write(Path.Combine(directory, "after-replay.json"), run!.Sessions());
                    Write(Path.Combine(directory, "controlled-aliases.json"), run!.ReplayAliases(buyers));
                    Write(Path.Combine(directory, "after-alias-replay.json"), run!.Sessions());
                    run!.Rebuild(); Write(Path.Combine(directory, "after-restart.json"), run!.Sessions());
                    Console.WriteLine(single ? "RESTORED: single buyer; identity/messages/activity/state/dedupe unchanged." :
                        "RESTORED: both buyers; identities/messages/activity/state/dedupe unchanged.");
                }
                Write(Path.Combine(directory, "progress.json"), new { Stage = stage, Buyer = BuyerFor(stage), Messages = texts[stage] });
                Console.WriteLine($"STAGE_READY: {stage}; Buyer {BuyerFor(stage)}; send: {string.Join(" / ", texts[stage])}");
                string? previousHash = null;
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(single ? 15 : 4));
                while (run is null || !run.Complete(stage))
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    Require(!File.Exists(Path.Combine(directory, "stop.request")), "Operator stopped acceptance.");
                    var current = capture.FindWindows(_ => { }).SingleOrDefault(w => w.Hwnd == window.Hwnd && w.Pid == window.Pid &&
                        w.WindowTitle == window.WindowTitle && w.IsVisible && !w.IsMinimized && !w.IsCloaked);
                    Require(current is not null, "Selected reception window changed or became unavailable.");
                    var slot = Path.Combine(directory, "frames", (++frame).ToString("D4"));
                    Directory.CreateDirectory(slot);
                    var native = new NativeWorkerClient();
                    var result = await native.CaptureAsync(current!, slot, deadline.Token);
                    Write(Path.Combine(slot, "capture.json"), result);
                    if (result.Success && result.SavedPath is not null)
                    {
                        var layout = await new QianniuLayoutResolver().ResolveAsync(result.SavedPath);
                        Write(Path.Combine(slot, "layout.json"), layout);
                        new QianniuRegionExtractor().Extract(result.SavedPath, slot, layout.ToPixelLayout());
                        var chat = Path.Combine(slot, "chat_region.png");
                        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(chat)));
                        if (hash != previousHash)
                        {
                            var ocr = await native.RecognizeAsync(chat, deadline.Token);
                            var batch = new BuyerMessageStructurer().Structure(ocr);
                            Write(Path.Combine(slot, "buyer-messages.json"), batch);
                            var selected = Select(batch, prefix); var raw = SessionRegistry.ReadBuyer(selected, ocr);
                            string? normalized = null;
                            if (raw is not null) try { normalized = normalizer.Normalize(raw); } catch (ArgumentException) { }
                            if (single && run is null && selected.Messages.Count > 0 && normalized is not null)
                            {
                                // Bind only from the real header of this run's marked buyer input; never from message text.
                                buyers = new(raw!, ""); labels["A"] = normalized;
                                run = new Run(database, buyers, prefix, single: true, messagePlan: texts); Metadata();
                            }
                            var buyer = labels.FirstOrDefault(p => p.Value == normalized).Key;
                            Write(Path.Combine(slot, "identity.json"), new { Stage = stage, Buyer = buyer, BuyerLabel = raw,
                                NormalizedBuyerLabel = normalized, IdentityLevel = "Observed" });
                            if (selected.Messages.Count > 0 && buyer == BuyerFor(stage))
                            {
                                await foreach (var envelope in new OcrMessageSource(_ => Task.FromResult(selected), seller, run!.BuyerIds[buyer]).ReadAsync(deadline.Token))
                                {
                                    Write(Path.Combine(slot, "envelope.json"), envelope);
                                    var context = run!.Accept(stage, envelope);
                                    Write(Path.Combine(slot, "context.json"), context);
                                    receipts.Add(new(stage, buyer, Path.GetRelativePath(directory, slot), envelope, context));
                                    Console.WriteLine($"CONTEXT_SAVED: {stage}; Buyer {buyer}; messages={context.Messages.Count}");
                                }
                            }
                            else if (selected.Messages.Count > 0) Console.WriteLine($"IDENTITY_HELD: {stage}; OCR buyer not confirmed.");
                            previousHash = hash;
                        }
                    }
                    if (run is null || !run.Complete(stage)) await Task.Delay(1500, deadline.Token);
                }
                run!.Replay(); Write(Path.Combine(directory, stage + ".json"), run!.Sessions());
                Console.WriteLine($"STAGE_COMPLETE: {stage}");
            }
            Require(run is not null && run!.Sessions().Length == (single ? 1 : 2) &&
                run!.Sessions().Sum(s => s.Messages.Count) == (single ? 3 : 7), "Unexpected final Session/message count.");
            status = "Passed";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            status = ex is OperationCanceledException ? "Incomplete" : "Failed";
            failure = ex is OperationCanceledException ? "Stage deadline expired; real messages not fully captured." : ex.Message;
            Console.Error.WriteLine($"ACCEPTANCE_{status.ToUpperInvariant()}: {failure}");
        }
        finally
        {
            Write(Path.Combine(directory, "receipts.json"), receipts);
            Write(Path.Combine(directory, "summary.json"), new
            {
                Status = status, Failure = failure, EndedAt = DateTimeOffset.UtcNow, CompletedFrames = frame,
                AcceptanceScope = single ? "SingleBuyerReal" : "MultiBuyerReal",
                MultiBuyerRealStatus = single ? "Pending" : status,
                CompletedStages = stages.Where(stage => run is not null && run.Complete(stage)).ToArray(),
                Sessions = run?.Sessions().Length ?? 0, StoredMessages = run?.Sessions().Sum(s => s.Messages.Count) ?? 0, AiCalls = 0, IntentCalls = 0,
                KnowledgeCalls = 0, ReplyCalls = 0, SentMessages = 0, SendEnabled = false
            });
            run?.Dispose();
            if (run is not null)
            using (var checkpoint = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = run!.Database, Pooling = false }.ToString()))
            {
                checkpoint.Open(); using var command = checkpoint.CreateCommand();
                command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);"; command.ExecuteNonQuery();
            }
            var hashes = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                .Where(p => !p.EndsWith("-wal", StringComparison.Ordinal) && !p.EndsWith("-shm", StringComparison.Ordinal))
                .ToDictionary(p => Path.GetRelativePath(directory, p), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
            Write(Path.Combine(directory, "evidence-hashes.json"), hashes);
        }
        return status == "Passed" ? 0 : 3;
    }

    public static async Task<int> ResumeSingleAsync(string directory, string capturedFrame)
    {
        directory = Path.GetFullPath(directory); capturedFrame = Path.GetFullPath(capturedFrame);
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "session.json")));
        using var previousSummary = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "summary.json")));
        var m = metadata.RootElement;
        Require(m.GetProperty("EvidenceKind").GetString() == "LiveQianniuSingleBuyerOCR" &&
            previousSummary.RootElement.GetProperty("Status").GetString() == "Incomplete",
            "Resume requires incomplete real single-buyer evidence.");
        var buyers = m.GetProperty("Buyers").Deserialize<Buyers>()!;
        Require(buyers.BuyerB == "", "Resume cannot imply a second real buyer.");
        var prefix = m.GetProperty("Prefix").GetString()!;
        var plan = m.GetProperty("ExpectedMessages").Deserialize<Dictionary<string, string[]>>()!;
        var database = Path.Combine(directory, "sessions.db");
        var expected = Read<AgentSession[]>(Path.Combine(directory, "after-restart.json"));
        Require(expected.Length == 1 && expected[0].Messages.Count == 2, "Resume requires completed initial pair and rebuild.");
        var capture = Read<CaptureResult>(Path.Combine(capturedFrame, "capture.json"));
        Require(capture.Success && capture.SavedPath is not null && File.Exists(capture.SavedPath), "Missing successful real capture.");
        var ocr = Read<OcrResult>(Path.Combine(capturedFrame, "chat_region.png.worker.json"));
        var batch = Select(new BuyerMessageStructurer().Structure(ocr), prefix);
        var raw = SessionRegistry.ReadBuyer(batch, ocr);
        var normalized = new IdentityNormalizer().Normalize(buyers.BuyerA);
        Require(raw is not null && new IdentityNormalizer().Normalize(raw) == normalized, "Resume OCR buyer mismatch.");
        var envelope = Read<MessageEnvelope>(Path.Combine(capturedFrame, "envelope.json"));
        var rebuilt = new List<MessageEnvelope>();
        await foreach (var e in new OcrMessageSource(_ => Task.FromResult(batch), expected[0].Identity.SellerId,
            Observed("buyer", normalized)).ReadAsync()) rebuilt.Add(e);
        Require(rebuilt.Count == 1 && JsonSerializer.Serialize(rebuilt[0]) == JsonSerializer.Serialize(envelope),
            "Resume Envelope differs from original OCR adapter output.");
        Require(envelope.Messages.Any(e => plan["A4"].Contains(e.Text, StringComparer.Ordinal)), "No real recovered new message in capture.");
        var receipts = Read<Receipt[]>(Path.Combine(directory, "receipts.json")).ToList();
        var oldHashes = Read<Dictionary<string, string>>(Path.Combine(directory, "evidence-hashes.json"));
        Require(oldHashes["sessions.db"] == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(database))), "Original database changed since incomplete capture.");
        foreach (var name in new[] { "summary.json", "receipts.json", "evidence-hashes.json", "sessions.db" })
            File.Copy(Path.Combine(directory, name), Path.Combine(directory, "pre-resume-" + name), overwrite: false);
        using (var run = new Run(database, buyers, prefix, single: true, messagePlan: plan, resume: true))
        {
            Require(Snapshot(run.Sessions()) == Snapshot(expected), "Restored current-wall-clock state differs from original rebuild snapshot.");
            run.Envelopes.AddRange(receipts.Select(r => r.Envelope));
            run.Replay();
            Write(Path.Combine(directory, "after-resume-restore.json"), run.Sessions());
            var slot = Path.Combine(directory, "frames", "resume-0001");
            Directory.CreateDirectory(slot);
            foreach (var file in Directory.GetFiles(capturedFrame)) File.Copy(file, Path.Combine(slot, Path.GetFileName(file)));
            var context = run.Accept("A4", envelope);
            Require(context.SessionId == expected[0].SessionId && context.Messages.Count == 3 &&
                context.CreatedTime == expected[0].CreatedTime && context.LastActiveTime > expected[0].LastActiveTime,
                "Recovered append changed Session or did not advance real activity.");
            Write(Path.Combine(slot, "context.json"), context);
            receipts.Add(new("A4", "A", Path.GetRelativePath(directory, slot), envelope, context));
            run.Replay(); Write(Path.Combine(directory, "A4.json"), run.Sessions());
            Write(Path.Combine(directory, "receipts.json"), receipts);
            Write(Path.Combine(directory, "resume.json"), new
            {
                ResumedAt = DateTimeOffset.UtcNow, PreviousStatus = "Incomplete", CapturedFrame = capturedFrame,
                SameDatabase = database, SameSessionId = context.SessionId, NewMessageSource = "RealQianniuOCR",
                IdentityLevel = "Observed", MultiBuyerRealStatus = "Pending"
            });
            Write(Path.Combine(directory, "summary.json"), new
            {
                Status = "Passed", Failure = (string?)null, EndedAt = DateTimeOffset.UtcNow,
                CompletedFrames = previousSummary.RootElement.GetProperty("CompletedFrames").GetInt32() + 1,
                AcceptanceScope = "SingleBuyerReal", MultiBuyerRealStatus = "Pending",
                CompletedStages = SingleStages, Sessions = 1, StoredMessages = 3,
                AiCalls = 0, IntentCalls = 0, KnowledgeCalls = 0, ReplyCalls = 0, SentMessages = 0, SendEnabled = false
            });
        }
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);"; command.ExecuteNonQuery();
        }
        Write(Path.Combine(directory, "evidence-hashes.json"), Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Where(p => Path.GetFileName(p) != "evidence-hashes.json" && !p.EndsWith("-wal", StringComparison.Ordinal) && !p.EndsWith("-shm", StringComparison.Ordinal))
            .ToDictionary(p => Path.GetRelativePath(directory, p), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))));
        Console.WriteLine("SINGLE_REAL_RESUMED: original SQLite/Session; real new message appended; messages=3; Observed; MultiBuyerReal=Pending.");
        return 0;
    }

    public static async Task EvidenceAsync(string directory, Action<bool, string> check, bool single = false)
    {
        directory = Path.GetFullPath(directory);
        using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "session.json")));
        using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "summary.json")));
        var m = meta.RootElement; var s = summary.RootElement;
        var stages = single ? SingleStages : Stages;
        check(m.GetProperty("EvidenceKind").GetString() == (single ? "LiveQianniuSingleBuyerOCR" : "LiveQianniuOCR") && m.GetProperty("Mode").GetString() == "ShadowContextOnly", "live Context-only evidence");
        check(s.GetProperty("Status").GetString() == "Passed", "all real stages completed; partial evidence cannot pass");
        check(new[] { "AiCalls", "IntentCalls", "KnowledgeCalls", "ReplyCalls", "SentMessages" }.All(k => s.GetProperty(k).GetInt32() == 0) &&
            !s.GetProperty("SendEnabled").GetBoolean(), "five zero counters and send disabled");
        var buyers = m.GetProperty("Buyers").Deserialize<Buyers>()!; var n = new IdentityNormalizer();
        var labels = new Dictionary<string, string> { ["A"] = n.Normalize(buyers.BuyerA) };
        if (single)
            check(s.GetProperty("MultiBuyerRealStatus").GetString() == "Pending" && buyers.BuyerB == "",
                "single real evidence leaves multi-buyer real acceptance Pending without a fabricated Buyer B");
        else
        {
            labels["B"] = n.Normalize(buyers.BuyerB);
            check(labels["A"] != labels["B"], "two operator-confirmed candidates remain distinct");
        }
        var ids = labels.ToDictionary(p => p.Key, p => Observed("buyer", p.Value));
        var prefix = m.GetProperty("Prefix").GetString()!;
        var texts = m.TryGetProperty("ExpectedMessages", out var expectedMessages)
            ? expectedMessages.Deserialize<Dictionary<string, string[]>>()! : Messages(prefix);
        var seller = Observed("seller", m.GetProperty("WindowTitle").GetString()!);
        var receipts = Read<Receipt[]>(Path.Combine(directory, "receipts.json"));
        check(receipts.Length > 0 && stages.All(stage => receipts.Any(r => r.Stage == stage)), "real receipts cover every required stage for the selected scope");
        check(receipts.Select(r => Array.IndexOf(stages, r.Stage)).SequenceEqual(receipts.Select(r => Array.IndexOf(stages, r.Stage)).Order()),
            single ? "receipt order proves continuous input then recovered append" : "receipt order proves A/B/A and recovered A/B append");
        foreach (var receipt in receipts)
        {
            Require(receipt.Buyer == BuyerFor(receipt.Stage), "Receipt stage/buyer mismatch.");
            var slot = Path.GetFullPath(Path.Combine(directory, receipt.Frame));
            Require(slot.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Frame escapes evidence root.");
            var capture = Read<CaptureResult>(Path.Combine(slot, "capture.json"));
            check(capture.Success && capture.SavedPath is not null && File.Exists(capture.SavedPath) && File.Exists(Path.Combine(slot, "chat_region.png")), "raw capture and chat PNG retained");
            var ocr = Read<OcrResult>(Path.Combine(slot, "chat_region.png.worker.json"));
            var batch = Select(new BuyerMessageStructurer().Structure(ocr), prefix); var raw = SessionRegistry.ReadBuyer(batch, ocr);
            check(raw is not null && n.Normalize(raw) == labels[receipt.Buyer], "raw OCR header matches normalized buyer");
            var original = Read<MessageEnvelope>(Path.Combine(slot, "envelope.json"));
            var rebuilt = new List<MessageEnvelope>();
            await foreach (var e in new OcrMessageSource(_ => Task.FromResult(batch), seller, ids[receipt.Buyer]).ReadAsync()) rebuilt.Add(e);
            check(rebuilt.Count == 1 && JsonSerializer.Serialize(rebuilt[0]) == JsonSerializer.Serialize(original) &&
                JsonSerializer.Serialize(original) == JsonSerializer.Serialize(receipt.Envelope), "unchanged OCR adapter reproduces live Envelope");
            check(original.SchemaVersion == 1 && original.Source == "Qianniu.OCR" && original.BuyerId == ids[receipt.Buyer] &&
                original.SellerId == seller, "OCR-derived Stable Identity scope preserved");
            ValidateContext(original, receipt.Context);
            check(Equal(Read<AgentSession>(Path.Combine(slot, "context.json")), receipt.Context), "Context and stable IDs match receipt");
        }
        var sessionIds = new Dictionary<string, string>();
        foreach (var stage in stages)
        {
            var sessions = Read<AgentSession[]>(Path.Combine(directory, stage + ".json")); var index = Array.IndexOf(stages, stage);
            foreach (var buyer in single || index == 0 ? new[] { "A" } : new[] { "A", "B" })
            {
                var expected = stages.Take(index + 1).Where(x => BuyerFor(x) == buyer).SelectMany(x => texts[x]).ToArray();
                var session = sessions.Single(x => x.Identity.BuyerId == ids[buyer]);
                if (sessionIds.TryGetValue(buyer, out var originalId)) check(session.SessionId == originalId, $"{stage}: Buyer {buyer} original SessionId");
                else sessionIds[buyer] = session.SessionId;
                check(session.Messages.Select(x => x.Content).SequenceEqual(expected) && session.Messages.All(x => x.SessionId == session.SessionId) &&
                    session.Messages.Select(x => x.MessageId).Distinct().Count() == expected.Length, $"{stage}: Buyer {buyer} exact isolated transcript and distinct IDs");
                check(session.Identity.IdentityLevel == IdentityLevel.Observed && session.Identity.SellerId == seller &&
                    session.Identity.Source == "Qianniu.OCR" && session.Status == AgentSessionStatus.Active, $"{stage}: Buyer {buyer} scope/Observed/Active preserved");
            }
            check(sessions.Length == (single || index == 0 ? 1 : 2), $"{stage}: no unexplained Session");
        }
        var before = Read<AgentSession[]>(Path.Combine(directory, "before-restart.json"));
        check(Snapshot(before) == Snapshot(Read<AgentSession[]>(Path.Combine(directory, "after-replay.json"))), "replay preserves SQLite and LastActiveTime");
        check(Snapshot(before) == Snapshot(Read<AgentSession[]>(Path.Combine(directory, "after-restart.json"))), "rebuild and restored-index replay preserve full snapshots");
        check(Snapshot(before) == Snapshot(Read<AgentSession[]>(Path.Combine(directory, "after-alias-replay.json"))), "controlled alias replay preserves original real Context");
        var aliases = Read<Alias[]>(Path.Combine(directory, "controlled-aliases.json"));
        check(aliases.Length == (single ? 4 : 8) && aliases.All(a => n.Normalize(a.Raw) == a.Normalized && a.Normalized == labels[a.Buyer]),
            "separately labeled controlled nickname variants use original normalization rules");
        var final = Read<AgentSession[]>(Path.Combine(directory, single ? "A4.json" : "B3.json"));
        check(final.All(x => x.SessionId == before.Single(b => b.Identity == x.Identity).SessionId),
            single ? "restored single buyer appends to original SessionId" : "restored A/B append to original SessionIds");
        check(final.All(x => x.CreatedTime == before.Single(b => b.Identity == x.Identity).CreatedTime &&
            x.LastActiveTime > before.Single(b => b.Identity == x.Identity).LastActiveTime), "recovered new input advances activity and preserves creation time");
        if (File.Exists(Path.Combine(directory, "resume.json")))
        {
            check(Snapshot(Read<AgentSession[]>(Path.Combine(directory, "after-resume-restore.json"))) == Snapshot(before),
                "later component recreation with actual wall clock preserves original restored state");
            var archivedHashes = Read<Dictionary<string, string>>(Path.Combine(directory, "pre-resume-evidence-hashes.json"));
            using var archivedSummary = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "pre-resume-summary.json")));
            check(archivedSummary.RootElement.GetProperty("Status").GetString() == "Incomplete" &&
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, "pre-resume-sessions.db")))) == archivedHashes["sessions.db"],
                "original incomplete status and two-message database remain preserved with their original hash");
        }
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(directory, "sessions.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        object? Scalar(string sql) { using var c = connection.CreateCommand(); c.CommandText = sql; return c.ExecuteScalar(); }
        check(Convert.ToInt32(Scalar("PRAGMA user_version;")) == 1 && (string?)Scalar("PRAGMA integrity_check;") == "ok", "schema v1 and SQLite integrity verified");
        check(Convert.ToInt32(Scalar("SELECT COUNT(*) FROM Sessions;")) == (single ? 1 : 2) && Convert.ToInt32(Scalar("SELECT COUNT(*) FROM Messages;")) == (single ? 3 : 7) &&
            Convert.ToInt32(Scalar("SELECT COUNT(*) FROM Sessions WHERE IdentityLevel <> 0;")) == 0 && Scalar("PRAGMA foreign_key_check;") is null, "SQLite: expected Observed Session/message counts and valid foreign keys");
        check(Snapshot(new SQLiteAgentSessionStore(Path.Combine(directory, "sessions.db")).LoadSessions()) == Snapshot(final), single ? "SQLite equals final single-buyer snapshot" : "SQLite equals final A4/B3 snapshots");
        foreach (var hash in Read<Dictionary<string, string>>(Path.Combine(directory, "evidence-hashes.json")))
        {
            var path = Path.GetFullPath(Path.Combine(directory, hash.Key));
            Require(path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Hash path escapes evidence root.");
            Require(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) == hash.Value, "Evidence hash mismatch.");
        }
        check(true, "retained capture/Envelope/Context/replay/recovery hashes verified");
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 3, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    public static Task RunAsync(Action<bool, string> check)
    {
        var root = Path.GetFullPath(Path.Combine("Output", "ValidationV1340C", "checks", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root); var clock = new Clock();
        MessageEnvelope Envelope(Run run, string stage) => new("Qianniu.OCR", "observed:seller:synthetic", run.BuyerIds[BuyerFor(stage)], "",
            run.Texts[stage].Select((text, i) => new EnvelopeMessage("buyer", text, clock.Now.UtcDateTime.AddSeconds(i))).ToArray());
        using var run = new Run(Path.Combine(root, "synthetic.db"), new("Test Buyer A", "Test Buyer B"), "13400000", clock);
        foreach (var stage in Stages)
        {
            if (stage == "A4")
            {
                var before = Snapshot(run.Sessions()); clock.Now = clock.Now.AddMinutes(1); run.Replay();
                check(Snapshot(run.Sessions()) == before, "synthetic pre-restart replay preserves LastActiveTime and SQLite");
                check(run.ReplayAliases(new("Test Buyer A", "Test Buyer B")).Length == 8 && Snapshot(run.Sessions()) == before,
                    "controlled aliases preserve both real-shaped envelope keys and SQLite snapshots");
                run.Rebuild(); check(Snapshot(run.Sessions()) == before, "synthetic rebuild restores both buyers and dedupe");
            }
            clock.Now = clock.Now.AddSeconds(10); var context = run.Accept(stage, Envelope(run, stage));
            check(run.Complete(stage), $"synthetic {stage}: controlled messages complete");
            check(context.Identity.IdentityLevel == IdentityLevel.Observed, $"synthetic {stage}: Observed");
            var beforeReplay = Snapshot(run.Sessions()); clock.Now = clock.Now.AddSeconds(5); run.Replay();
            check(Snapshot(run.Sessions()) == beforeReplay, $"synthetic {stage}: replay idempotent after clock advances");
        }
        check(run.Sessions().Length == 2 && run.Sessions().Sum(s => s.Messages.Count) == 7, "synthetic protocol: two Sessions/seven messages");
        void Reject(Action action, string message)
        {
            var before = Snapshot(run.Sessions());
            try { action(); } catch (InvalidDataException) { check(Snapshot(run.Sessions()) == before, message); return; }
            check(false, message);
        }
        Reject(() => run.Accept("A4", Envelope(run, "B3")), "cross-buyer stage delivery rejected before mutation");
        Reject(() => run.Accept("A4", Envelope(run, "A4") with { Messages = [new("buyer", "unreviewed OCR", clock.Now.UtcDateTime)] }), "unreviewed text rejected before mutation");
        try { using var collision = new Run(Path.Combine(root, "collision.db"), new("Tom", "ＴＯＭ！"), "13400000", clock); check(false, "candidate collision blocks capture"); }
        catch (InvalidDataException) { check(!File.Exists(Path.Combine(root, "collision.db")), "candidate collision blocks capture before DB creation"); }
        try { using var reused = new Run(run.Database, new("A", "B"), "13400000", clock); check(false, "existing DB rejected"); }
        catch (InvalidDataException) { check(run.Sessions().Sum(s => s.Messages.Count) == 7, "existing DB rejected without changes"); }
        using (var unstable = new Run(Path.Combine(root, "duplicate-timestamp.db"), new("A", "B"), "13400000", clock))
        {
            var original = Envelope(unstable, "A1A2"); unstable.Accept("A1A2", original);
            var stopped = false;
            try { unstable.Accept("A1A2", original with { Messages = [original.Messages[0] with { Timestamp = original.Messages[0].Timestamp.AddSeconds(1) }] }); }
            catch (InvalidDataException) { stopped = true; }
            check(stopped && unstable.Sessions().Single().Messages.Count == 3,
                "timestamp drift stops acceptance and retains the duplicate evidence rather than hiding it");
        }
        var partial = Path.Combine(root, "partial"); Directory.CreateDirectory(partial);
        Write(Path.Combine(partial, "session.json"), new { EvidenceKind = "LiveQianniuOCR", Mode = "ShadowContextOnly" });
        Write(Path.Combine(partial, "summary.json"), new { Status = "Incomplete" }); var rejected = false;
        try { EvidenceAsync(partial, (condition, _) => { if (!condition) throw new InvalidDataException(); }).GetAwaiter().GetResult(); }
        catch (InvalidDataException) { rejected = true; }
        check(rejected, "partial live evidence cannot pass");
        Console.WriteLine($"MULTI_BUYER_SYNTHETIC_ONLY: Database={run.Database}; no live acceptance claim; AI/Intent/Knowledge/Reply/SentMessages=0");
        return Task.CompletedTask;
    }
    public static async Task RunSingleAsync(Action<bool, string> check)
    {
        var root = Path.GetFullPath(Path.Combine("Output", "ValidationV1340C", "single-checks", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root); var clock = new Clock(); var buyers = new Buyers("Test Buyer A", "");
        using var run = new Run(Path.Combine(root, "synthetic.db"), buyers, "13400001", clock, single: true);
        MessageEnvelope Envelope(string stage) => new("Qianniu.OCR", "observed:seller:synthetic", run.BuyerIds["A"], "",
            run.Texts[stage].Select((text, i) => new EnvelopeMessage("buyer", text, clock.Now.UtcDateTime.AddSeconds(i))).ToArray());
        check(run.BuyerIds.Count == 1 && !run.BuyerIds.ContainsKey("B"), "single scope never fabricates Buyer B");
        var first = Envelope("A1A2");
        var a1 = run.Accept("A1A2", first with { Messages = [first.Messages[0]] });
        check(a1.Messages.Count == 1 && !run.Complete("A1A2"), "single first message adds one Context item");
        clock.Now = clock.Now.AddSeconds(10);
        var a2 = run.Accept("A1A2", first);
        check(a2.SessionId == a1.SessionId && a2.Messages.Count == 2 && run.Complete("A1A2"), "single continuous second message appends to original Session");
        var before = Snapshot(run.Sessions()); clock.Now = clock.Now.AddMinutes(1); run.Replay();
        check(Snapshot(run.Sessions()) == before, "single replay preserves SQLite/messages/LastActiveTime");
        check(run.ReplayAliases(buyers).Length == 4 && Snapshot(run.Sessions()) == before, "single controlled OCR aliases preserve Observed key and Context");
        run.Rebuild(); check(Snapshot(run.Sessions()) == before, "single rebuild restores full state and duplicate index");
        var after = run.Accept("A4", Envelope("A4"));
        check(after.SessionId == a1.SessionId && after.Messages.Count == 3 && after.Identity.IdentityLevel == IdentityLevel.Observed,
            "single recovered new message appends to original Observed Session");
        var partial = Path.Combine(root, "partial"); Directory.CreateDirectory(partial);
        Write(Path.Combine(partial, "session.json"), new { EvidenceKind = "LiveQianniuSingleBuyerOCR", Mode = "ShadowContextOnly" });
        Write(Path.Combine(partial, "summary.json"), new { Status = "Incomplete" });
        var rejected = false;
        try { await EvidenceAsync(partial, (condition, _) => { if (!condition) throw new InvalidDataException(); }, single: true); }
        catch (InvalidDataException) { rejected = true; }
        check(rejected, "partial single real evidence cannot pass");
        rejected = false;
        try { await EvidenceAsync(partial, (condition, _) => { if (!condition) throw new InvalidDataException(); }); }
        catch (InvalidDataException) { rejected = true; }
        check(rejected, "single real evidence cannot be promoted to multi-buyer real acceptance");
        using (var configured = new Run(Path.Combine(root, "configured.db"), buyers, "13400001", clock, single: true,
            messagePlan: new Dictionary<string, string[]> { ["A1A2"] = ["1340000101规格测试", "13400001020容量测试"], ["A4"] = ["1340000104恢复测试"] }))
        {
            var envelope = new MessageEnvelope("Qianniu.OCR", "observed:seller:synthetic", configured.BuyerIds["A"], "",
                configured.Texts["A1A2"].Select((text, i) => new EnvelopeMessage("buyer", text, clock.Now.UtcDateTime.AddSeconds(i))).ToArray());
            check(configured.Accept("A1A2", envelope).Messages[1].Content == envelope.Messages[1].Text,
                "explicit controlled-message plan preserves actual input without rewriting Envelope text");
        }
        var resumeDatabase = Path.Combine(root, "resume.db");
        AgentSession seeded;
        var seedEnvelope = new MessageEnvelope("Qianniu.OCR", "observed:seller:synthetic", run.BuyerIds["A"], "", first.Messages);
        using (var seed = new Run(resumeDatabase, buyers, "13400001", clock, single: true)) seeded = seed.Accept("A1A2", seedEnvelope);
        using (var resumed = new Run(resumeDatabase, buyers, "13400001", clock, single: true, resume: true))
        {
            resumed.Envelopes.Add(seedEnvelope); resumed.Replay();
            var appended = resumed.Accept("A4", Envelope("A4"));
            check(appended.SessionId == seeded.SessionId && appended.Messages.Count == 3,
                "single evidence resume opens same dedicated SQLite and appends to original Session");
        }
        Console.WriteLine($"SINGLE_BUYER_SYNTHETIC_ONLY: Database={run.Database}; no live acceptance claim; MultiBuyerReal=Pending; AI/Intent/Knowledge/Reply/SentMessages=0");
    }

}
