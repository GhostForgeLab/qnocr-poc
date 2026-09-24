namespace QnocrPoc.Models;

public sealed record ProductProfile(string ProductId, string ProfileKey, string DisplayName, bool IsTestData,
    IReadOnlyDictionary<string, string>? Attributes = null);

public sealed record ProductProfileCatalog(int SchemaVersion, bool IsTestCatalog, IReadOnlyList<ProductProfile> Profiles);
