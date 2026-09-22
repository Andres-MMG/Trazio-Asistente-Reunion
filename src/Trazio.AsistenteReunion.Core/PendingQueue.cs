using System.Threading.Channels;

namespace Trazio.AsistenteReunion.Core;

public sealed class PendingAudioQueue
{
    private readonly Channel<AudioChunk> _channel;
    private readonly long _maxBytes;
    private readonly long _maxChunksPerSource;
    private long _bytes;
    private readonly Dictionary<AudioSourceKind, long> _counts = [];
    private readonly Dictionary<AudioSourceKind, long> _leasedCounts = [];
    private long _leasedBytes;
    private long _leasedCount;
    private readonly Lock _gate = new();
    private readonly Func<CancellationToken, Task>? _afterLeaseTransfer;

    public PendingAudioQueue(int maxMinutesPerSource = 5, long maxBytes = 64 * 1024 * 1024, Func<CancellationToken, Task>? afterLeaseTransfer = null)
    {
        _maxChunksPerSource = maxMinutesPerSource * 60L;
        _maxBytes = maxBytes;
        _afterLeaseTransfer = afterLeaseTransfer;
        _channel = Channel.CreateBounded<AudioChunk>(new BoundedChannelOptions((int)Math.Min(int.MaxValue, _maxChunksPerSource * 2))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public long PendingBytes { get { lock (_gate) return _bytes + _leasedBytes; } }
    public long QueuedCount { get { lock (_gate) return _counts.Values.Sum(); } }
    public long LeasedCount { get { lock (_gate) return _leasedCount; } }
    public long OutstandingCount { get { lock (_gate) return _counts.Values.Sum() + _leasedCount; } }

    public bool TryEnqueue(AudioChunk chunk)
    {
        lock (_gate)
        {
            var count = _counts.GetValueOrDefault(chunk.Source);
            var totalSourceCount = count + _leasedCounts.GetValueOrDefault(chunk.Source);
            if (totalSourceCount >= _maxChunksPerSource || _bytes + _leasedBytes + chunk.Pcm16.Length > _maxBytes)
                return false;
            if (!_channel.Writer.TryWrite(chunk)) return false;
            _counts[chunk.Source] = count + 1;
            _bytes += chunk.Pcm16.Length;
            return true;
        }
    }

    public async ValueTask<PendingAudioLease> LeaseAsync(CancellationToken cancellationToken = default)
    {
        var chunk = await _channel.Reader.ReadAsync(cancellationToken);
        lock (_gate)
        {
            _counts[chunk.Source]--;
            _bytes -= chunk.Pcm16.Length;
            _leasedCount++;
            _leasedBytes += chunk.Pcm16.Length;
            _leasedCounts[chunk.Source] = _leasedCounts.GetValueOrDefault(chunk.Source) + 1;
        }
        var lease = new PendingAudioLease(this, chunk);
        try
        {
            if (_afterLeaseTransfer is not null) await _afterLeaseTransfer(cancellationToken);
            return lease;
        }
        catch
        {
            lease.Abandon();
            throw;
        }
    }

    private void ReleaseLease(AudioChunk chunk)
    {
        lock (_gate)
        {
            _leasedCount--;
            _leasedBytes -= chunk.Pcm16.Length;
            _leasedCounts[chunk.Source]--;
        }
    }

    public sealed class PendingAudioLease
    {
        private readonly PendingAudioQueue _owner;
        private int _released;
        internal PendingAudioLease(PendingAudioQueue owner, AudioChunk chunk) { _owner = owner; Chunk = chunk; }
        public AudioChunk Chunk { get; }
        public void Complete() => Release();
        public void Abandon() => Release();
        private void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) _owner.ReleaseLease(Chunk);
        }
    }
}

public static class TranscriptOverlap
{
    public static string Merge(string existing, string incoming)
    {
        if (string.IsNullOrWhiteSpace(existing)) return incoming.Trim();
        if (string.IsNullOrWhiteSpace(incoming)) return existing.Trim();
        var left = existing.Trim();
        var right = incoming.Trim();
        var max = Math.Min(left.Length, right.Length);
        for (var length = max; length >= 8; length--)
        {
            if (left.EndsWith(right[..length], StringComparison.OrdinalIgnoreCase))
                return left + right[length..];
        }
        return $"{left} {right}";
    }
}
