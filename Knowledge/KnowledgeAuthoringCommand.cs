using System.Text.Json;

namespace QnocrPoc.Knowledge;

/// <summary>Local test-only operator commands; no background import or automatic publishing.</summary>
public static class KnowledgeAuthoringCommand
{
    public static int Run(string[] args)
    {
        if (args.Length == 4 && args[0] == "--knowledge-draft")
        {
            var review = new KnowledgeReviewService(args[2]);
            var draft = new KnowledgeAuthoringService(args[2], review).CreateFromFile(args[1], args[3]);
            Console.WriteLine(JsonSerializer.Serialize(draft));
            return draft.Status == Models.KnowledgeDraftStatus.Conflict ? 3 : 0;
        }
        if (args.Length == 3 && args[0] == "--knowledge-show")
        {
            var review = new KnowledgeReviewService(args[1]);
            Console.WriteLine(JsonSerializer.Serialize(new KnowledgeAuthoringService(args[1], review).GetDraft(args[2])));
            return 0;
        }
        if (args.Length >= 5 && args[0] == "--knowledge-review")
        {
            var review = new KnowledgeReviewService(args[2]);
            var item = args[1] switch
            {
                "submit" when args.Length == 5 => review.SubmitForReview(args[3], args[4]),
                "approve" when args.Length == 5 => review.Approve(args[3], args[4]),
                "reject" when args.Length == 6 => review.Reject(args[3], args[4], args[5]),
                "publish" when args.Length == 6 && args[5] == "--allow-ai-use" =>
                    review.Publish(args[3], args[4], allowAiUse: true),
                _ => throw new ArgumentException("Use submit/approve/reject or explicit publish --allow-ai-use.")
            };
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                item.KnowledgeId, item.SellerId, item.ProductId, item.Key,
                item.Status, item.AiUseAllowed, item.UpdatedAt
            }));
            return 0;
        }
        throw new ArgumentException("Invalid knowledge authoring command.");
    }
}
