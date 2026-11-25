using Microsoft.Extensions.Options;
using Optio.Telemetry;

namespace Optio.Streams;

/// <summary>The number of open streams of each user, for the limit
/// <see cref="OptioOptions.MaxStreamsPerUser"/>. It is the only state of the stream in
/// memory.</summary>
internal sealed class OpenStreams(IOptions<OptioOptions> options, OptioMetrics metrics)
{
    private readonly Dictionary<ulong, int> _users = [];

    /// <summary>Counts one more stream of <paramref name="user"/>. False when the user has the
    /// maximum.</summary>
    public bool TryOpen(ulong user)
    {
        lock (_users)
        {
            var count = _users.GetValueOrDefault(user);
            if (count >= options.Value.MaxStreamsPerUser)
                return false;
            _users[user] = count + 1;
        }
        metrics.StreamOpened();
        return true;
    }

    public void Close(ulong user)
    {
        lock (_users)
        {
            if (_users.GetValueOrDefault(user) is var count and > 1)
                _users[user] = count - 1;
            else
                _users.Remove(user);
        }
        metrics.StreamClosed();
    }
}
