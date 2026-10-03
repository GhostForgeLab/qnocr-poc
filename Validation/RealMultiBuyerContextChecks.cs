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
        public Run(string database, Buyers buyers, string prefix, TimeProvider? clock = null)
        {
            var normalizer = new IdentityNormalizer();
            var a = normalizer.Normalize(buyers.BuyerA); var b = normalizer.Normalize(buyers.BuyerB);
            Require(a != b, "Test buyers collapse to one Observed candidate; stop, do not merge.");
            BuyerIds = new(StringComparer.Ordinal) { ["A"] = Observed("buyer", a), ["B"] = Observed("buyer", b) };
            Texts = Messages(prefix); Database = Path.GetFullPath(database);
            Require(!File.Exists(Database), "Acceptance requires a fresh dedicated test database.");
            _clock = clock; _pipeline = new(Database, clock);
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
            foreach (var (buyer, label) in new[] { ("A", buyers.BuyerA), ("B", buyers.BuyerB) })
                foreach (var raw in new[] { " " + label + " ", label + "!", label.Normalize(NormalizationForm.FormKC), label.Normalize(NormalizationForm.FormKD) })
                {
                    var normalized = n.Normalize(raw); var id = Observed("buyer", normalized);
                    Require(id == BuyerIds[buyer], "Controlled alias changed normalized buyer key.");
                    foreach (var envelope in Envelopes.Where(e => e.BuyerId == BuyerIds[buyer]))
                        _pipeline.ProcessMessage(envelope with { BuyerId = id });
                    aliases.Add(new(buyer, raw, normalized));
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

    public static async Task<int> CaptureAsync(string buyersPath, string directory, string prefix)
    {
        directory = Path.GetFullPath(directory);
        Require(prefix.Length >= 6 && prefix.All(char.IsAsciiDigit), "Use a unique numeric prefix of at least six digits.");
        Require(!Directory.Exists(directory) && !File.Exists(directory), "Use a new evidence directory.");
        var buyers = Read<Buyers>(buyersPath); var normalizer = new IdentityNormalizer();
        var labels = new Dictionary<string, string> { ["A"] = normalizer.Normalize(buyers.BuyerA), ["B"] = normalizer.Normalize(buyers.BuyerB) };
        Require(labels["A"] != labels["B"], "Distinct buyers normalize identically; blocked before capture.");
        var capture = new QianniuWindowCapture();
        var windows = capture.FindWindows(_ => { }).Where(w => w.IsVisible && !w.IsMinimized && !w.IsCloaked &&
            w.WindowTitle.Contains("接待中心", StringComparison.Ordinal)).ToArray();
        Require(windows.Length == 1, "Exactly one visible reception window is required.");
        var window = windows.Single(); var seller = Observed("seller", window.WindowTitle);
        Directory.CreateDirectory(directory);
        using var run = new Run(Path.Combine(directory, "sessions.db"), buyers, prefix);
        Write(Path.Combine(directory, "session.json"), new
        {
            Version = "1.34.0-C", Mode = "ShadowContextOnly", EvidenceKind = "LiveQianniuOCR",
            StartedAt = DateTimeOffset.UtcNow, Prefix = prefix, DatabasePath = run.Database,
            WindowHandle = $"{window.Hwnd:X}", window.Pid, window.WindowTitle, SellerId = seller, Buyers = buyers,
            IdentityLevel = "Observed", CounterBasis = "No business dependencies or dispatch/send entrypoints in this validation path"
        });
        Write(Path.Combine(directory, "instructions.json"), run.Texts);
        var receipts = new List<Receipt>(); var status = "Incomplete"; string? failure = null; var frame = 0;
        try
        {
            foreach (var stage in Stages)
            {
                if (stage == "A4")
                {
                    Write(Path.Combine(directory, "before-restart.json"), run.Sessions());
                    run.Replay(); Write(Path.Combine(directory, "after-replay.json"), run.Sessions());
                    Write(Path.Combine(directory, "controlled-aliases.json"), run.ReplayAliases(buyers));
                    Write(Path.Combine(directory, "after-alias-replay.json"), run.Sessions());
                    run.Rebuild(); Write(Path.Combine(directory, "after-restart.json"), run.Sessions());
                    Console.WriteLine("RESTORED: both buyers; identities/messages/activity/state/dedupe unchanged.");
                }
                Write(Path.Combine(directory, "progress.json"), new { Stage = stage, Buyer = BuyerFor(stage), Messages = run.Texts[stage] });
                Console.WriteLine($"STAGE_READY: {stage}; Buyer {BuyerFor(stage)}; send: {string.Join(" / ", run.Texts[stage])}");
                string? previousHash = null;
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
                while (!run.Complete(stage))
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
                            var buyer = labels.FirstOrDefault(p => p.Value == normalized).Key;
                            Write(Path.Combine(slot, "identity.json"), new { Stage = stage, Buyer = buyer, BuyerLabel = raw,
                                NormalizedBuyerLabel = normalized, IdentityLevel = "Observed" });
                            if (selected.Messages.Count > 0 && buyer == BuyerFor(stage))
                            {
                                await foreach (var envelope in new OcrMessageSource(_ => Task.FromResult(selected), seller, run.BuyerIds[buyer]).ReadAsync(deadline.Token))
                                {
                                    Write(Path.Combine(slot, "envelope.json"), envelope);
                                    var context = run.Accept(stage, envelope);
                                    Write(Path.Combine(slot, "context.json"), context);
                                    receipts.Add(new(stage, buyer, Path.GetRelativePath(directory, slot), envelope, context));
                                    Console.WriteLine($"CONTEXT_SAVED: {stage}; Buyer {buyer}; messages={context.Messages.Count}");
                                }
                            }
                            else if (selected.Messages.Count > 0) Console.WriteLine($"IDENTITY_HELD: {stage}; OCR buyer not confirmed.");
                            previousHash = hash;
                        }
                    }
                    if (!run.Complete(stage)) await Task.Delay(1500, deadline.Token);
                }
                run.Replay(); Write(Path.Combine(directory, stage + ".json"), run.Sessions());
                Console.WriteLine($"STAGE_COMPLETE: {stage}");
            }
            Require(run.Sessions().Length == 2 && run.Sessions().Sum(s => s.Messages.Count) == 7, "Expected two Sessions/seven messages.");
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
                CompletedStages = Stages.Where(run.Complete).ToArray(), Sessions = run.Sessions().Length,
                StoredMessages = run.Sessions().Sum(s => s.Messages.Count), AiCalls = 0, IntentCalls = 0,
                KnowledgeCalls = 0, ReplyCalls = 0, SentMessages = 0, SendEnabled = false
            });
            run.Dispose();
            using (var checkpoint = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = run.Database, Pooling = false }.ToString()))
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

    public static async Task EvidenceAsync(string directory, Action<bool, string> check)
    {
        directory = Path.GetFullPath(directory);
        using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "session.json")));
        using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "summary.json")));
        var m = meta.RootElement; var s = summary.RootElement;
        check(m.GetProperty("EvidenceKind").GetString() == "LiveQianniuOCR" && m.GetProperty("Mode").GetString() == "ShadowContextOnly", "live Context-only evidence");
        check(s.GetProperty("Status").GetString() == "Passed", "all real stages completed; partial evidence cannot pass");
        check(new[] { "AiCalls", "IntentCalls", "KnowledgeCalls", "ReplyCalls", "SentMessages" }.All(k => s.GetProperty(k).GetInt32() == 0) &&
            !s.GetProperty("SendEnabled").GetBoolean(), "five zero counters and send disabled");
        var buyers = m.GetProperty("Buyers").Deserialize<Buyers>()!; var n = new IdentityNormalizer();
        var labels = new Dictionary<string, string> { ["A"] = n.Normalize(buyers.BuyerA), ["B"] = n.Normalize(buyers.BuyerB) };
        check(labels["A"] != labels["B"], "two operator-confirmed candidates remain distinct");
        var ids = labels.ToDictionary(p => p.Key, p => Observed("buyer", p.Value));
        var prefix = m.GetProperty("Prefix").GetString()!; var texts = Messages(prefix);
        var seller = Observed("seller", m.GetProperty("WindowTitle").GetString()!);
        var receipts = Read<Receipt[]>(Path.Combine(directory, "receipts.json"));
        check(receipts.Length > 0 && Stages.All(stage => receipts.Any(r => r.Stage == stage)), "real receipts cover all five stages");
        check(receipts.Select(r => Array.IndexOf(Stages, r.Stage)).SequenceEqual(receipts.Select(r => Array.IndexOf(Stages, r.Stage)).Order()),
            "receipt order proves A/B/A and recovered A/B append");
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
        foreach (var stage in Stages)
        {
            var sessions = Read<AgentSession[]>(Path.Combine(directory, stage + ".json")); var index = Array.IndexOf(Stages, stage);
            foreach (var buyer in index == 0 ? new[] { "A" } : new[] { "A", "B" })
            {
                var expected = Stages.Take(index + 1).Where(x => BuyerFor(x) == buyer).SelectMany(x => texts[x]).ToArray();
                var session = sessions.Single(x => x.Identity.BuyerId == ids[buyer]);
                if (sessionIds.TryGetValue(buyer, out var originalId)) check(session.SessionId == originalId, $"{stage}: Buyer {buyer} original SessionId");
                else sessionIds[buyer] = session.SessionId;
                check(session.Messages.Select(x => x.Content).SequenceEqual(expected) && session.Messages.All(x => x.SessionId == session.SessionId) &&
                    session.Messages.Select(x => x.MessageId).Distinct().Count() == expected.Length, $"{stage}: Buyer {buyer} exact isolated transcript and distinct IDs");
                check(session.Identity.IdentityLevel == IdentityLevel.Observed && session.Identity.SellerId == seller &&
                    session.Identity.Source == "Qianniu.OCR" && session.Status == AgentSessionStatus.Active, $"{stage}: Buyer {buyer} scope/Observed/Active preserved");
            }
            check(sessions.Length == (index == 0 ? 1 : 2), $"{stage}: no unexplained Session");
        }
        var before = Read<AgentSession[]>(Path.Combine(directory, "before-restart.json"));
        check(Snapshot(before) == Snapshot(Read<AgentSession[]>(Path.Combine(directory, "after-replay.json"))), "replay preserves SQLite and LastActiveTime");
        check(Snapshot(before) == Snapshot(Read<AgentSession[]>(Path.Combine(directory, "after-restart.json"))), "rebuild and restored-index replay preserve full snapshots");
        check(Snapshot(before) == Snapshot(Read<AgentSession[]>(Path.Combine(directory, "after-alias-replay.json"))), "controlled alias replay preserves original real Context");
        var aliases = Read<Alias[]>(Path.Combine(directory, "controlled-aliases.json"));
        check(aliases.Length == 8 && aliases.All(a => n.Normalize(a.Raw) == a.Normalized && a.Normalized == labels[a.Buyer]),
            "eight separately labeled controlled nickname variants use original normalization rules");
        var final = Read<AgentSession[]>(Path.Combine(directory, "B3.json"));
        check(final.All(x => x.SessionId == before.Single(b => b.Identity == x.Identity).SessionId), "restored A/B append to original SessionIds");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(directory, "sessions.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        object? Scalar(string sql) { using var c = connection.CreateCommand(); c.CommandText = sql; return c.ExecuteScalar(); }
        check(Convert.ToInt32(Scalar("PRAGMA user_version;")) == 1 && (string?)Scalar("PRAGMA integrity_check;") == "ok", "schema v1 and SQLite integrity verified");
        check(Convert.ToInt32(Scalar("SELECT COUNT(*) FROM Sessions;")) == 2 && Convert.ToInt32(Scalar("SELECT COUNT(*) FROM Messages;")) == 7 &&
            Convert.ToInt32(Scalar("SELECT COUNT(*) FROM Sessions WHERE IdentityLevel <> 0;")) == 0 && Scalar("PRAGMA foreign_key_check;") is null, "SQLite: two Observed Sessions/seven messages/valid foreign keys");
        check(Snapshot(new SQLiteAgentSessionStore(Path.Combine(directory, "sessions.db")).LoadSessions()) == Snapshot(final), "SQLite equals final A4/B3 snapshots");
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
}
