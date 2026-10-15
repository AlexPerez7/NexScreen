using Vortice.Direct3D11;

namespace NexScreen.Core.Video.Native;

/// <summary>
/// Un encoder H.264 de hardware que recibe texturas NV12 de la misma GPU. Se usa siempre desde el
/// mismo hilo (el de captura), así que no necesita ser seguro entre hilos.
/// </summary>
internal interface IGpuEncoder : IDisposable
{
    string Name { get; }

    /// <summary>SPS y PPS en Annex-B, cuando el encoder ya los conoce (puede ser null hasta el primer keyframe).</summary>
    byte[]? ParameterSets { get; }

    /// <summary>
    /// Codifica una textura NV12 y agrega a <paramref name="output"/> los cuadros que ya están listos
    /// (con encoders de baja latencia, uno por cuadro).
    /// </summary>
    void Encode(ID3D11Texture2D nv12, long ptsUs, bool forceKeyFrame, List<EncodedFrame> output);

    /// <summary>Cambia el bitrate objetivo sin reiniciar el encoder.</summary>
    void SetBitrate(int mbps);
}
