using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Outbox.Relay.Endpoints;

public sealed class StreamHub
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Subscriber>> _subs = new();

    public sealed class Subscriber
    {
        public Guid Id { get; } = Guid.NewGuid();
        public Channel<string> Channel { get; } = System.Threading.Channels.Channel.CreateBounded<string>(
            new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
    }

    public Subscriber Add(string handle)
    {
        var sub = new Subscriber();
        var dict = _subs.GetOrAdd(handle, _ => new ConcurrentDictionary<Guid, Subscriber>());
        dict[sub.Id] = sub;
        return sub;
    }

    public void Remove(string handle, Subscriber sub)
    {
        if (_subs.TryGetValue(handle, out var dict))
        {
            dict.TryRemove(sub.Id, out _);
            sub.Channel.Writer.TryComplete();
        }
    }

    public void Broadcast(string handle, string eventName, string dataJson)
    {
        if (!_subs.TryGetValue(handle, out var dict)) return;
        var payload = $"event: {eventName}\ndata: {dataJson}\n\n";
        foreach (var sub in dict.Values)
            sub.Channel.Writer.TryWrite(payload);
    }
}
