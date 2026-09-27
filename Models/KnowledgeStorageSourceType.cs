using System.Text.Json.Serialization;

namespace QnocrPoc.Models;

/// <summary>Storage adapter identity, distinct from a fact's provenance (KnowledgeSourceType).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<KnowledgeStorageSourceType>))]
public enum KnowledgeStorageSourceType { LocalFile, Feishu, Database, Obsidian }
