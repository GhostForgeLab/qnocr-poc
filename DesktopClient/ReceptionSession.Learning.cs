using System;
using System.Linq;
using System.Threading.Tasks;

namespace QNAgent;

internal sealed partial class ReceptionSession
{
    private void CollectLearning(InboxMessage seller)
    {
        try
        {
            if (_selected is null || _replyProduct.SelectedItem is not CatalogProduct product) return;
            var preview = _replies.Latest(_selected);
            if (preview is null) return;
            var service = new LearningService(_shared);
            var candidate = service.Collect(_selected, seller, product, preview, _messages.ToArray());
            if (candidate is null) return;
            var task = AnalyzeLaterAsync(service, candidate); _operations.Add(task);
            _ = task.ContinueWith(_ => Post(() => _operations.Remove(task)), TaskScheduler.Default);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        { SetStatus("卖家回复已记录；学习建议暂未收集，可稍后到学习建议页检查。"); }
    }
    private async Task AnalyzeLaterAsync(LearningService service, LearningCandidate candidate)
    {
        try
        {
            await Task.Delay(2000, _lifetime.Token);
            await service.AnalyzeAsync(candidate.Id, _lifetime.Token, candidate.SampleHash);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is not OutOfMemoryException) { }
    }
}
