using System.Collections.Generic;
using QnocrPoc.Models;

namespace QNAgent;

// Connection-scoped LRU. Keys include exact chat + header pixels; retain text only, never screenshots.
internal sealed class ChatRecognitionCache
{
    private readonly LinkedList<(string Key, OcrResult Value)> _items = new();
    private const int Capacity = 24;
    public bool TryGet(string key, out OcrResult result)
    {
        for (var node = _items.First; node is not null; node = node.Next)
        {
            if (node.Value.Key != key) continue;
            result = node.Value.Value; _items.Remove(node); _items.AddFirst(node); return true;
        }
        result = null!; return false;
    }
    public void Add(string key, OcrResult value)
    {
        if (TryGet(key, out _)) _items.RemoveFirst();
        _items.AddFirst((key, value));
        if (_items.Count > Capacity) _items.RemoveLast();
    }
}
