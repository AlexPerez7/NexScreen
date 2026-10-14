namespace NexScreen.Core.Video;

[Flags]
internal enum FrameFlags : byte
{
    None = 0,
    KeyFrame = 1,

    /// <summary>El frame solo contiene parámetros del codec (SPS/PPS).</summary>
    Config = 2,
}

/// <summary>Un access unit H.264 en formato Annex-B (start codes 00 00 00 01).</summary>
internal readonly record struct EncodedFrame(byte[] Data, long PtsUs, FrameFlags Flags);

internal interface IVideoSource : IAsyncDisposable
{
    string Name { get; }

    IAsyncEnumerable<EncodedFrame> ReadFramesAsync(CancellationToken ct);

    /// <summary>Últimas líneas de diagnóstico, para reportar por qué falló la fuente.</summary>
    string Diagnostics { get; }
}
