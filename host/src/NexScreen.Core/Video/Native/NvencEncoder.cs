using System.Runtime.InteropServices;
using Vortice.Direct3D11;

namespace NexScreen.Core.Video.Native;

/// <summary>
/// H.264 con NVENC directo (sin ffmpeg): sesión D3D11, preset P1 con ajuste de latencia ultra baja,
/// CBR sin B-frames, y bitrate y keyframes que se cambian en marcha.
/// </summary>
internal sealed unsafe class NvencEncoder : IGpuEncoder
{
    // Tamaños de los buffers (de sobra: NVENC escribe según su propia versión de la estructura).
    private const int PresetBufferSize = 8192;
    private const int InitParamsSize = 1808;
    // Regiones del buffer de trabajo (no se usan a la vez): estructuras chicas, SPS/PPS, NV_ENC_PIC_PARAMS y NV_ENC_LOCK_BITSTREAM.
    private const int SmallRegion = 0;
    private const int SequenceRegion = 2048;
    private const int SmallSize = 2048;
    private const int PicRegion = 4096;
    private const int PicSize = 4096;
    private const int LockRegion = 8192;
    private const int LockSize = 2048;
    private const int ScratchSize = 10240;

    // --- Desplazamientos dentro de NV_ENC_CONFIG (nvEncodeAPI.h 11.1, x64)
    private const int CfgProfileGuid = 4;
    private const int CfgGopLength = 20;
    private const int CfgFrameIntervalP = 24;
    private const int CfgRcMode = 44;
    private const int CfgRcAverageBitRate = 60;
    private const int CfgRcMaxBitRate = 64;
    private const int CfgRcVbvBufferSize = 68;
    private const int CfgRcVbvInitialDelay = 72;
    private const int CfgRcBitfield = 76;
    private const int CfgRcMultiPass = 140;
    private const int CfgH264Bitfield = 168;
    private const int CfgH264IdrPeriod = 176;
    private const int CfgH264ChromaFormatIdc = 360;
    private const int CfgH264VuiVideoSignalTypePresent = 248;
    private const int CfgH264VuiVideoFormat = 252;
    private const int CfgH264VuiFullRange = 256;
    private const int CfgH264VuiColourDescriptionPresent = 260;
    private const int CfgH264VuiColourPrimaries = 264;
    private const int CfgH264VuiTransfer = 268;
    private const int CfgH264VuiMatrix = 272;

    private const uint RcEnableLookahead = 1u << 5;
    private const uint RcZeroReorderDelay = 1u << 9;
    private const uint H264RepeatSpsPps = 1u << 12;

    private readonly NvEncApi _api;
    private readonly int _fps;
    private readonly int _width;
    private readonly int _height;
    private readonly Dictionary<nint, nint> _registered = [];

    private void* _encoder;
    private byte* _presetBuffer;
    private byte* _initParams;
    private byte* _bitstream;
    private byte* _scratch;
    private uint _frameIndex;
    private bool _disposed;

    private NvencEncoder(NvEncApi api, int width, int height, int fps)
    {
        _api = api;
        _width = width;
        _height = height;
        _fps = fps;
    }

    public string Name => "NVIDIA NVENC";

    public byte[]? ParameterSets { get; private set; }

    private byte* Config => _presetBuffer + 8;

    public static NvencEncoder Create(NvEncApi api, ID3D11Device device, int width, int height, int fps, int bitrateMbps)
    {
        var encoder = new NvencEncoder(api, width, height, fps);
        try
        {
            encoder.Open(device, bitrateMbps);
            return encoder;
        }
        catch
        {
            encoder.Dispose();
            throw;
        }
    }

    private void Open(ID3D11Device device, int bitrateMbps)
    {
        _scratch = (byte*)NativeMemory.AllocZeroed(ScratchSize);
        _presetBuffer = (byte*)NativeMemory.AllocZeroed(PresetBufferSize);
        _initParams = (byte*)NativeMemory.AllocZeroed(InitParamsSize);

        // La sesión usa el dispositivo D3D11 dueño de las texturas.
        var open = _scratch + SmallRegion;
        new Span<byte>(open, SmallSize).Clear();
        *(uint*)open = NvEncApi.StructVersion(1);
        *(uint*)(open + 4) = 0; // NV_ENC_DEVICE_TYPE_DIRECTX
        *(nint*)(open + 8) = device.NativePointer;
        *(uint*)(open + 24) = 11u | (1u << 24);
        void* encoder = null;
        _api.Check(_api.OpenSessionEx(open, &encoder), "abrir la sesión", encoder);
        _encoder = encoder;

        // Preset P1 (el más rápido) con ajuste de latencia ultra baja: es la base, después se afina.
        *(uint*)_presetBuffer = NvEncApi.StructVersion(4) | (1u << 31);
        *(uint*)Config = NvEncApi.StructVersion(7) | (1u << 31);
        _api.Check(
            _api.GetPresetConfigEx(_encoder, NvEncApi.CodecH264, NvEncApi.PresetP1, NvEncApi.TuningUltraLowLatency, _presetBuffer),
            "leer el preset", _encoder);

        ApplyTuning(bitrateMbps);

        var init = _initParams;
        *(uint*)init = NvEncApi.StructVersion(5) | (1u << 31);
        *(Guid*)(init + 4) = NvEncApi.CodecH264;
        *(Guid*)(init + 20) = NvEncApi.PresetP1;
        *(uint*)(init + 36) = (uint)_width;
        *(uint*)(init + 40) = (uint)_height;
        *(uint*)(init + 44) = (uint)_width;
        *(uint*)(init + 48) = (uint)_height;
        *(uint*)(init + 52) = (uint)_fps;
        *(uint*)(init + 56) = 1;
        *(uint*)(init + 60) = 0; // sincrónico
        *(uint*)(init + 64) = 1; // el encoder decide el tipo de cada cuadro (enablePTD)
        *(nint*)(init + 88) = (nint)Config;
        *(uint*)(init + 96) = (uint)_width;
        *(uint*)(init + 100) = (uint)_height;
        *(uint*)(init + 136) = NvEncApi.TuningUltraLowLatency;
        *(uint*)(init + 140) = NvEncApi.BufferFormatNv12;
        _api.Check(_api.InitializeEncoder(_encoder, init), "inicializar el encoder", _encoder);

        var buffer = _scratch + SmallRegion;
        new Span<byte>(buffer, SmallSize).Clear();
        *(uint*)buffer = NvEncApi.StructVersion(1);
        _api.Check(_api.CreateBitstreamBuffer(_encoder, buffer), "crear el buffer de salida", _encoder);
        _bitstream = (byte*)*(nint*)(buffer + 16);

        ParameterSets = ReadSequenceParameters();
    }

    /// <summary>Ajusta la configuración del preset a este uso: CBR, sin B-frames, GOP de 2 s y SPS/PPS en cada IDR.</summary>
    private void ApplyTuning(int bitrateMbps)
    {
        var config = Config;
        *(Guid*)(config + CfgProfileGuid) = NvEncApi.ProfileH264Main;
        *(uint*)(config + CfgGopLength) = (uint)(_fps * 2);
        *(int*)(config + CfgFrameIntervalP) = 1; // sin B-frames
        *(uint*)(config + CfgRcMode) = NvEncApi.RcModeCbr;
        *(uint*)(config + CfgRcMultiPass) = 0;
        *(uint*)(config + CfgRcBitfield) = (*(uint*)(config + CfgRcBitfield) & ~RcEnableLookahead) | RcZeroReorderDelay;
        ApplyBitrate(bitrateMbps);

        *(uint*)(config + CfgH264Bitfield) |= H264RepeatSpsPps;
        *(uint*)(config + CfgH264IdrPeriod) = (uint)(_fps * 2);
        *(uint*)(config + CfgH264ChromaFormatIdc) = 1;

        // El convertidor BGRA→NV12 usa BT.709 de rango limitado: lo declara el stream para que la
        // tablet decodifique con la misma matriz.
        *(uint*)(config + CfgH264VuiVideoSignalTypePresent) = 1;
        *(uint*)(config + CfgH264VuiVideoFormat) = 5;
        *(uint*)(config + CfgH264VuiFullRange) = 0;
        *(uint*)(config + CfgH264VuiColourDescriptionPresent) = 1;
        *(uint*)(config + CfgH264VuiColourPrimaries) = 1;
        *(uint*)(config + CfgH264VuiTransfer) = 1;
        *(uint*)(config + CfgH264VuiMatrix) = 1;
    }

    private void ApplyBitrate(int bitrateMbps)
    {
        var config = Config;
        var bitrate = (uint)(bitrateMbps * 1_000_000);
        // Buffer de 4 cuadros: deja que un keyframe sea más grande que el promedio sin retrasar el envío.
        var vbv = bitrate / (uint)_fps * 4;
        *(uint*)(config + CfgRcAverageBitRate) = bitrate;
        *(uint*)(config + CfgRcMaxBitRate) = bitrate;
        *(uint*)(config + CfgRcVbvBufferSize) = vbv;
        *(uint*)(config + CfgRcVbvInitialDelay) = vbv;
    }

    private byte[]? ReadSequenceParameters()
    {
        var payload = _scratch + SmallRegion;
        var buffer = _scratch + SequenceRegion;
        uint size = 0;
        new Span<byte>(payload, SmallSize).Clear();
        *(uint*)payload = NvEncApi.StructVersion(1);
        *(uint*)(payload + 4) = 1024;
        *(nint*)(payload + 16) = (nint)buffer;
        *(nint*)(payload + 24) = (nint)(&size);
        if (_api.GetSequenceParams(_encoder, payload) != NvEncApi.StatusSuccess || size == 0) return null;
        return new ReadOnlySpan<byte>(buffer, (int)size).ToArray();
    }

    public void SetBitrate(int mbps)
    {
        ApplyBitrate(mbps);
        var reconfigure = (byte*)NativeMemory.AllocZeroed(InitParamsSize + 64);
        try
        {
            *(uint*)reconfigure = NvEncApi.StructVersion(1) | (1u << 31);
            Buffer.MemoryCopy(_initParams, reconfigure + 8, InitParamsSize, InitParamsSize);
            _api.Check(_api.ReconfigureEncoder(_encoder, reconfigure), "cambiar el bitrate", _encoder);
        }
        finally
        {
            NativeMemory.Free(reconfigure);
        }
    }

    public void Encode(ID3D11Texture2D nv12, long ptsUs, bool forceKeyFrame, List<EncodedFrame> output)
    {
        var registered = Register(nv12);

        var map = _scratch + SmallRegion;
        new Span<byte>(map, SmallSize).Clear();
        *(uint*)map = NvEncApi.StructVersion(4);
        *(nint*)(map + 16) = registered;
        _api.Check(_api.MapInputResource(_encoder, map), "mapear la textura", _encoder);
        var mapped = *(nint*)(map + 24);
        var mappedFormat = *(uint*)(map + 32);

        try
        {
            var pic = _scratch + PicRegion;
            new Span<byte>(pic, PicSize).Clear();
            *(uint*)pic = NvEncApi.StructVersion(4) | (1u << 31);
            *(uint*)(pic + 4) = (uint)_width;
            *(uint*)(pic + 8) = (uint)_height;
            *(uint*)(pic + 12) = (uint)_width;
            *(uint*)(pic + 16) = forceKeyFrame ? NvEncApi.PicFlagForceIdr | NvEncApi.PicFlagOutputSpsPps : 0;
            *(uint*)(pic + 20) = _frameIndex++;
            *(long*)(pic + 24) = ptsUs;
            *(nint*)(pic + 40) = mapped;
            *(nint*)(pic + 48) = (nint)_bitstream;
            *(uint*)(pic + 64) = mappedFormat;
            *(uint*)(pic + 68) = 1; // NV_ENC_PIC_STRUCT_FRAME

            var status = _api.EncodePicture(_encoder, pic);
            if (status == NvEncApi.StatusNeedMoreInput) return;
            _api.Check(status, "codificar", _encoder);

            var lockParams = _scratch + LockRegion;
            new Span<byte>(lockParams, LockSize).Clear();
            *(uint*)lockParams = NvEncApi.StructVersion(1);
            *(nint*)(lockParams + 8) = (nint)_bitstream;
            _api.Check(_api.LockBitstream(_encoder, lockParams), "leer el bitstream", _encoder);
            try
            {
                var size = (int)*(uint*)(lockParams + 36);
                var data = new ReadOnlySpan<byte>((void*)*(nint*)(lockParams + 56), size).ToArray();
                var isIdr = *(uint*)(lockParams + 64) == NvEncApi.PicTypeIdr;
                output.Add(new EncodedFrame(data, ptsUs, isIdr ? FrameFlags.KeyFrame : FrameFlags.None));
            }
            finally
            {
                _api.UnlockBitstream(_encoder, _bitstream);
            }
        }
        finally
        {
            _api.UnmapInputResource(_encoder, (void*)mapped);
        }
    }

    private nint Register(ID3D11Texture2D texture)
    {
        if (_registered.TryGetValue(texture.NativePointer, out var existing)) return existing;

        var register = _scratch + SmallRegion;
        new Span<byte>(register, SmallSize).Clear();
        *(uint*)register = NvEncApi.StructVersion(3);
        *(uint*)(register + 4) = 0; // NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX
        *(uint*)(register + 8) = (uint)_width;
        *(uint*)(register + 12) = (uint)_height;
        *(uint*)(register + 16) = 0;
        *(nint*)(register + 24) = texture.NativePointer;
        *(uint*)(register + 40) = NvEncApi.BufferFormatNv12;
        *(uint*)(register + 44) = 0; // NV_ENC_INPUT_IMAGE
        _api.Check(_api.RegisterResource(_encoder, register), "registrar la textura", _encoder);

        var handle = *(nint*)(register + 32);
        _registered[texture.NativePointer] = handle;
        return handle;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_encoder != null)
        {
            foreach (var handle in _registered.Values) _api.UnregisterResource(_encoder, (void*)handle);
            if (_bitstream != null) _api.DestroyBitstreamBuffer(_encoder, _bitstream);
            _api.DestroyEncoder(_encoder);
            _encoder = null;
        }

        _registered.Clear();
        if (_scratch != null) NativeMemory.Free(_scratch);
        if (_presetBuffer != null) NativeMemory.Free(_presetBuffer);
        if (_initParams != null) NativeMemory.Free(_initParams);
        _scratch = _presetBuffer = _initParams = _bitstream = null;
    }
}
