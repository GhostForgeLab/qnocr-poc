using QnocrPoc.Models;

namespace QnocrPoc.Decision;

public interface IIntentClassifier
{
    IntentResult Classify(SessionContextView context);
}
