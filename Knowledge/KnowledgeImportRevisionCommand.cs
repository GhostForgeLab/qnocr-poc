using System.Text.Json;
using System.Text.Json.Serialization;
using QnocrPoc.Models;

namespace QnocrPoc.Knowledge;

public sealed record KnowledgeRevisionInput(string SellerId, string ProductId, string Key,
    string NewValue, string Reason, string CandidateVersion, KnowledgeSource Source);

/// <summary>Explicit local test commands; no automatic review, publish, or send.</summary>
public static class KnowledgeImportRevisionCommand
{
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    public static int Run(string[] args)
    {
        if (args.Length == 6 && args[0] == "--knowledge-import")
        {
            var path = Path.GetFullPath(args[1]);
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is < 2 or > 16 * 1024)
                throw new InvalidDataException("One bounded test JSON file is required.");
            var now = DateTimeOffset.UtcNow;
            var source = new KnowledgeSource(KnowledgeSourceType.Import, "local-test-json",
                Path.GetFileName(path), now);
            var request = new KnowledgeImportRequest(Guid.NewGuid().ToString("N"), args[2], args[3],
                source, File.ReadAllText(path), now);
            var review = new KnowledgeReviewService(args[4]);
            var authoring = new KnowledgeAuthoringService(args[4], review);
            var draft = new KnowledgeImportService(args[4], authoring).Import(request, args[5]);
            Console.WriteLine(JsonSerializer.Serialize(new { request.ImportId, Draft = draft }));
            return draft.Status == KnowledgeDraftStatus.Conflict ? 3 : 0;
        }
        if (args.Length == 4 && args[0] == "--knowledge-revision")
        {
            var path = Path.GetFullPath(args[1]);
            if (new FileInfo(path).Length is < 2 or > 16 * 1024)
                throw new InvalidDataException("One bounded revision JSON file is required.");
            var input = JsonSerializer.Deserialize<KnowledgeRevisionInput>(File.ReadAllText(path), Options)
                ?? throw new InvalidDataException("Revision input is null.");
            var service = new KnowledgeRevisionService(args[2], new KnowledgeReviewService(args[2]));
            var revision = service.Create(input.SellerId, input.ProductId, input.Key, input.NewValue,
                input.Reason, input.CandidateVersion, input.Source, args[3]);
            Console.WriteLine(JsonSerializer.Serialize(revision));
            return revision.Status == KnowledgeRevisionStatus.Conflict ? 3 : 0;
        }
        if (args.Length == 3 && args[0] == "--knowledge-revision-show")
        {
            var service = new KnowledgeRevisionService(args[1], new KnowledgeReviewService(args[1]));
            Console.WriteLine(JsonSerializer.Serialize(service.GetRevision(args[2])));
            return 0;
        }
        if (args.Length == 4 && args[0] == "--knowledge-revision-publish")
        {
            var service = new KnowledgeRevisionService(args[1], new KnowledgeReviewService(args[1]));
            Console.WriteLine(JsonSerializer.Serialize(service.Publish(args[2], args[3])));
            return 0;
        }
        throw new ArgumentException("Invalid import/revision command.");
    }
}
