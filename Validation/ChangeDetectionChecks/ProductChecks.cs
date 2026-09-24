using System.Text.Json;
using QnocrPoc.Models;
using QnocrPoc.Product;

internal static class ProductChecks
{
    public static void Run(Action<bool, string> check)
    {
        const string id = "1051087533910";
        const string otherId = "1051087533911";
        var extractor = new ProductIdExtractor();
        static OcrLine Line(int number, string text, float? confidence = .98f, OcrBounds? bounds = null) =>
            new(number, text, confidence, bounds ?? new OcrBounds(10, number * 30, 300, 20));
        static OcrResult Ocr(params OcrLine[] lines) => new("synthetic.png", "Test OCR evidence", lines, 0, null, 0, 0, 0, 0, 0, 0);

        foreach (var text in new[] { "ID 1051087533910", "商品ID：1051087533910", "宝贝编号:1051087533910", "ＩＤ：１０５１０８７５３３９１０", "item_id: 1051087533910" })
        {
            var result = extractor.Extract(Ocr(Line(1, text)));
            check(result.Status == ProductIdStatus.Identified && result.ProductId == id, "labeled ID accepted: " + text);
        }
        foreach (var text in new[] { "1051087533910", "订单号:3316901269040012590", "订单 ID: 1051087533910", "会员ID1051087533910",
            "SKU ID: 1051087533910", "商品ID/标题/编码", "￥1051087533.90 库存21", "ID 1O51087533910", "ID 1051087533910abc",
            "ID 105108753391012345", "ID 1051087533 910", "ID 0000000000000" })
            check(extractor.Extract(Ocr(Line(1, text))).Status == ProductIdStatus.NotFound, "unrelated or malformed number rejected: " + text);

        var mixed = extractor.Extract(Ocr(Line(1, "订单 ID: 3316901269040; 商品ID:1051087533910")));
        check(mixed.ProductId == id, "explicit product field can coexist with a separate order field");
        var split = extractor.Extract(Ocr(Line(1, "ID", .93f, new(10, 20, 20, 20)), Line(2, id, .97f, new(35, 20, 150, 20))));
        check(split.ProductId == id && Math.Abs(split.Confidence!.Value - .93) < .0001 && split.Candidates[0].LineNumbers.Count == 2,
            "same-row split label and value use the lower supporting confidence");
        check(extractor.Extract(Ocr(Line(1, "ID", .99f, new(10, 20, 20, 20)), Line(2, id, .99f, new(35, 65, 150, 20)))).Status == ProductIdStatus.NotFound,
            "ID label does not attach a number on another row");
        check(extractor.Extract(Ocr(Line(1, "订单", .99f, new(0, 20, 40, 20)), Line(2, "ID", .99f, new(45, 20, 20, 20)), Line(3, id, .99f, new(70, 20, 150, 20)))).Status == ProductIdStatus.NotFound,
            "split order label cannot masquerade as product ID");
        check(extractor.Extract(Ocr(Line(1, "ＳＫＵ", .99f, new(0, 20, 40, 20)), Line(2, "ＩＤ", .99f, new(45, 20, 20, 20)), Line(3, id, .99f, new(70, 20, 150, 20)))).Status == ProductIdStatus.NotFound,
            "full-width split SKU label is also excluded");
        check(extractor.Extract(Ocr(Line(1, "商品ID/标题/编码", .99f, new(0, 20, 140, 20)), Line(2, id, .99f, new(145, 20, 150, 20)))).Status == ProductIdStatus.NotFound,
            "search placeholder does not serve as a split ID label");

        foreach (var score in new float?[] { .50f, null, float.NaN, float.PositiveInfinity, 1.1f })
        {
            var result = extractor.Extract(Ocr(Line(1, "ID " + id, score)));
            check(result.Status == ProductIdStatus.LowConfidence && result.ProductId is null, "low/missing/nonfinite confidence is not matched: " + score);
        }
        var repeated = extractor.Extract(Ocr(Line(1, "ID " + id, .85f), Line(2, "商品ID:" + id, .95f)));
        check(repeated.ProductId == id && repeated.Candidates.Count == 1 && repeated.Confidence < .96, "duplicate ID is deduplicated without accumulating confidence");
        var two = Ocr(Line(1, "ID " + id), Line(2, "ID " + otherId));
        check(extractor.Extract(two).Status == ProductIdStatus.Ambiguous, "multiple card IDs remain ambiguous");
        check(extractor.Extract(Ocr(Line(1, "ID " + id, .99f), Line(2, "ID " + otherId, .5f))).Status == ProductIdStatus.Ambiguous,
            "one high-confidence ID does not silently override another distinct ID");
        check(extractor.Extract(Ocr(Line(1, "ID 0123456789012"))).ProductId == "0123456789012", "IDs remain strings with leading zero preserved");
        check(extractor.Extract(Ocr()).Status == ProductIdStatus.NotFound, "empty OCR has no product context");

        var root = Path.GetFullPath(Path.Combine("Output", "ValidationV07"));
        Directory.CreateDirectory(root);
        var sample = Path.Combine(AppContext.BaseDirectory, "Product", "product-profiles.sample.json");
        var resolver = new ProductContextResolver(sample);
        var matched = resolver.Resolve(Ocr(Line(1, "ID " + id)));
        check(matched.Status == ProductContextStatus.Matched && matched.Profile?.ProfileKey == "poc-sample-1051087533910" && matched.IsTestCatalog && matched.Profile.IsTestData,
            "exact ID maps to explicitly marked sample ProductProfile");
        check(resolver.Resolve(Ocr(Line(1, "ID " + otherId))).Status == ProductContextStatus.Unmapped, "unknown exact ID remains Unmapped (no fuzzy catalog match)");
        check(resolver.Resolve(two).Status == ProductContextStatus.Ambiguous && resolver.Resolve(two).Profile is null,
            "catalog containing only one candidate cannot resolve multiple visible IDs");
        check(resolver.Resolve(Ocr(Line(1, "ID " + id, .5f))).Profile is null, "low-confidence evidence cannot map even a known ID");

        void RejectCatalog(string fileName, string json, string description)
        {
            var path = Path.Combine(root, fileName);
            File.WriteAllText(path, json);
            try { _ = new ProductContextResolver(path); }
            catch (Exception ex) when (ex is InvalidDataException or JsonException) { check(true, description); return; }
            check(false, description);
        }
        var profile = matched.Profile!;
        RejectCatalog("duplicate.json", JsonSerializer.Serialize(new ProductProfileCatalog(1, true, [profile, profile])), "duplicate catalog IDs are rejected");
        RejectCatalog("bad_schema.json", JsonSerializer.Serialize(new ProductProfileCatalog(2, true, [profile])), "unsupported catalog schema is rejected");
        RejectCatalog("bad_flags.json", JsonSerializer.Serialize(new ProductProfileCatalog(1, true, [profile with { IsTestData = false }])), "test catalog cannot silently include an unmarked profile");
        RejectCatalog("bad_id.json", JsonSerializer.Serialize(new ProductProfileCatalog(1, true, [profile with { ProductId = "TA1" }])), "malformed catalog ID is rejected");
        RejectCatalog("missing_profiles.json", "{\"SchemaVersion\":1}", "missing Profiles array is rejected");
        RejectCatalog("malformed.json", "not-json", "malformed catalog is rejected");
        var empty = Path.Combine(root, "empty-catalog.json");
        File.WriteAllText(empty, JsonSerializer.Serialize(new ProductProfileCatalog(1, true, [])));
        check(new ProductContextResolver(empty).Resolve(Ocr(Line(1, "ID " + id))).Status == ProductContextStatus.Unmapped, "valid empty catalog returns Unmapped");
    }
}
