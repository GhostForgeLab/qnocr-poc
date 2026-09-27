using QnocrPoc.Models;

namespace QnocrPoc.Feedback;

public interface IFeedbackRouter
{
    FeedbackClassification Classify(ReplyFeedback feedback);
}
