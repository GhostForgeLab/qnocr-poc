namespace QnocrPoc.Knowledge;

/// <summary>V1.19 constructor-compatible facade; all file reading lives in LocalFileKnowledgeSource.</summary>
public sealed class JsonProductKnowledgeRepository : ProductKnowledgeRepository
{
    public JsonProductKnowledgeRepository(string path, string? workflowItemsPath = null)
        : base(new LocalFileKnowledgeSource(path, workflowItemsPath)) { }
}
