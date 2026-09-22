using System.Buffers.Binary;
using System.Security.Cryptography;
using Trazio.AsistenteReunion.Core;

namespace Trazio.AsistenteReunion.App;

public static class AudioWaveformBuilder
{
    public const int DefaultBarCount = 120;

    public static TimeSpan GetTimelineDuration(DateTimeOffset sessionStartedAt, IReadOnlyList<ArchivedAudioChunk> chunks)
    {
        if (chunks.Count == 0) return TimeSpan.Zero;
        var end = chunks.Max(chunk => chunk.StartedAt + chunk.Duration);
        return end > sessionStartedAt ? end - sessionStartedAt : TimeSpan.Zero;
    }

    public static async Task<IReadOnlyList<double>> BuildAsync(
        AudioArchiveStore archive,
        DateTimeOffset sessionStartedAt,
        IReadOnlyList<ArchivedAudioChunk> chunks,
        int barCount = DefaultBarCount,
        CancellationToken cancellationToken = default)
    {
        if (barCount <= 0) throw new ArgumentOutOfRangeException(nameof(barCount));
        var duration = GetTimelineDuration(sessionStartedAt, chunks);
        if (duration <= TimeSpan.Zero) return [];
        var peaks = new double[barCount];
        var ordered = chunks.OrderBy(item => item.StartedAt).ThenBy(item => item.Sequence).ToArray();
        var sampled = ordered.Length <= barCount
            ? ordered
            : ordered
                .GroupBy(chunk =>
                {
                    var midpointSeconds = Math.Max(0, (chunk.StartedAt - sessionStartedAt + chunk.Duration / 2).TotalSeconds);
                    return Math.Min(barCount - 1, (int)(midpointSeconds / duration.TotalSeconds * barCount));
                })
                .Select(group => group.ElementAt(group.Count() / 2))
                .ToArray();
        foreach (var chunk in sampled)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wav = await archive.ReadChunkAsync(chunk, cancellationToken);
            try
            {
                var pcm = WavPcm.GetPcm16(wav).Span;
                var sampleRate = wav.Length >= 28 ? BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24, 4)) : 16_000;
                if (sampleRate <= 0) sampleRate = 16_000;
                var sampleCount = pcm.Length / 2;
                var stride = Math.Max(1, sampleCount / 4096);
                var chunkOffsetSeconds = Math.Max(0, (chunk.StartedAt - sessionStartedAt).TotalSeconds);
                for (var sample = 0; sample < sampleCount; sample += stride)
                {
                    var seconds = chunkOffsetSeconds + sample / (double)sampleRate;
                    var bucket = Math.Min(barCount - 1, (int)(seconds / duration.TotalSeconds * barCount));
                    var amplitude = Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(sample * 2, 2))) / 32768d;
                    if (amplitude > peaks[bucket]) peaks[bucket] = amplitude;
                }
            }
            finally { CryptographicOperations.ZeroMemory(wav); }
        }
        var maximum = peaks.Max();
        if (maximum <= 0) return Enumerable.Repeat(4d, barCount).ToArray();
        return peaks.Select(peak => 4d + 44d * Math.Sqrt(peak / maximum)).ToArray();
    }
}