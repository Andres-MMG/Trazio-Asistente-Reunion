using System.Buffers.Binary;

namespace Trazio.AsistenteReunion.Core;

public static class WavPcm
{
    public const int HeaderLength = 44;

    public static byte[] CreateMono16(byte[] pcm16, int sampleRate = 16_000)
    {
        var wav = new byte[HeaderLength + pcm16.Length];
        WriteHeader(wav.AsSpan(0, HeaderLength), pcm16.Length, sampleRate);
        pcm16.CopyTo(wav, HeaderLength);
        return wav;
    }

    public static ReadOnlyMemory<byte> GetPcm16(byte[] wav)
    {
        if (wav.Length < HeaderLength || !wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
            !wav.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException("El fragmento de audio archivado no es un archivo WAV.");
        var dataLength = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40, 4));
        if (dataLength < 0 || HeaderLength + dataLength > wav.Length)
            throw new InvalidDataException("El archivo WAV archivado está incompleto.");
        return wav.AsMemory(HeaderLength, dataLength);
    }

    public static void WriteHeader(Span<byte> header, long pcmBytes, int sampleRate = 16_000)
    {
        if (header.Length < HeaderLength || pcmBytes is < 0 or > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(pcmBytes));
        header.Clear();
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], checked((int)pcmBytes + 36));
        "WAVEfmt "u8.CopyTo(header[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(header[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(header[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], sampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(header[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(header[34..], 16);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..], checked((int)pcmBytes));
    }
}
