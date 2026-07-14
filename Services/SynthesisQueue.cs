namespace AIVoiceApi.Services;

public class SynthesisQueue
{
    private readonly object _lock = new();
    private readonly List<Item> _items = new();
    private long _seq;

    public Item Enqueue(SynthesisParams p, TaskCompletionSource<byte[]> tcs)
    {
        var priority = p.Priority;
        long seq;
        Item item;
        lock (_lock)
        {
            seq = ++_seq;
            item = new Item(p, tcs, priority, seq);
            _items.Add(item);
        }
        return item;
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

    public class Item
    {
        public SynthesisParams Params { get; }
        public TaskCompletionSource<byte[]> Tcs { get; }
        public int Priority { get; }
        public long Seq { get; }
        public DateTimeOffset EnqueuedAt { get; } = DateTimeOffset.UtcNow;
        public long QueueWaitMs { get; set; }
        public long SynthMs { get; set; }

        public Item(SynthesisParams p, TaskCompletionSource<byte[]> tcs, int priority, long seq)
        {
            Params = p;
            Tcs = tcs;
            Priority = priority;
            Seq = seq;
        }
    }
}
