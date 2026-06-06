namespace AIVoiceApi.Services;

public class SynthesisQueue
{
    private readonly object _lock = new();
    private readonly List<Item> _items = new();
    private long _seq;

    public void Enqueue(SynthesisParams p, TaskCompletionSource<byte[]> tcs)
    {
        var priority = p.Priority;
        long seq;
        lock (_lock)
        {
            seq = ++_seq;
            _items.Add(new Item(p, tcs, priority, seq));
        }
    }

    public Item? TryDequeue()
    {
        lock (_lock)
        {
            if (_items.Count == 0) return null;

            int bestIdx = 0;
            for (int i = 1; i < _items.Count; i++)
            {
                if (_items[i].Priority > _items[bestIdx].Priority)
                    bestIdx = i;
            }

            var item = _items[bestIdx];
            _items.RemoveAt(bestIdx);
            return item;
        }
    }

    public bool HasItems
    {
        get { lock (_lock) return _items.Count > 0; }
    }

    public record Item(
        SynthesisParams Params,
        TaskCompletionSource<byte[]> Tcs,
        int Priority,
        long Seq
    );
}
