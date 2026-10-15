using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace NexScreen.Core.Video.Native;

/// <summary>
/// H.264 con el encoder de hardware de Media Foundation (Intel Quick Sync, AMD). Es un MFT asíncrono:
/// pide cuadros y entrega salidas por eventos, que llegan a una cola por callback. Se usa desde un
/// solo hilo; <see cref="Encode"/> espera a que el encoder acepte el cuadro y recoge lo que ya terminó.
/// </summary>
internal sealed class MfH264Encoder : IGpuEncoder
{
    private static readonly Guid VideoEncoderCategory = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    private static readonly Guid FriendlyNameKey = new("314ffbae-5b41-4c95-9c19-4e7d586face3");
    private static readonly Guid MajorTypeKey = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid SubtypeKey = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid FrameSizeKey = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid FrameRateKey = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid AvgBitrateKey = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid InterlaceKey = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid ProfileKey = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    private static readonly Guid AsyncUnlockKey = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
    private static readonly Guid LowLatencyKey = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid YuvMatrixKey = new("3e23d450-2c75-4d25-a00e-b91670d12327");
    private static readonly Guid NominalRangeKey = new("c21b8ee5-b956-4071-8daf-325edf5cab11");
    private static readonly Guid PrimariesKey = new("dbfbe4d7-0740-4ee0-8192-850ab0e21935");
    private static readonly Guid TransferKey = new("5fb0fce9-be5c-4935-a811-ec838f8eed93");
    private static readonly Guid VideoMajor = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid H264 = new("34363248-0000-0010-8000-00AA00389B71");
    private static readonly Guid Nv12 = new("3231564E-0000-0010-8000-00AA00389B71");

    // CODECAPI_* (ICodecAPI)
    private const string CodecRateControlMode = "1c0608e9-370c-4710-8a58-cb6181c42423";
    private const string CodecMeanBitRate = "f7222374-2144-4815-b550-a37f8e12ee52";
    private const string CodecLowLatency = "9c27891a-ed7a-40e1-88e8-b22727a024ee";
    private const string CodecBFrameCount = "8d390aac-dc5c-4200-b57f-814d04babab2";
    private const string CodecGopSize = "95f31b26-95a4-41aa-9303-246a7fc6eef1";
    private const string CodecForceKeyFrame = "398c1b98-8353-475a-9ef2-8f265d260345";

    private const uint MftEnumHardware = 0x4;
    private const uint MftEnumSortAndFilter = 0x20;
    private const int EventNeedInput = 601;
    private const int EventHaveOutput = 602;
    private static readonly TimeSpan NeedInputTimeout = TimeSpan.FromSeconds(2);

    private static readonly object StartupLock = new();
    private static bool _started;

    private readonly int _fps;
    private readonly BlockingCollection<IMFMediaEvent> _events = [];
    private readonly IMFDXGIDeviceManager _manager;
    private IMFTransform? _transform;
    private IMFMediaEventGenerator? _generator;
    private EventCallback? _callback;
    private ICodecApi? _codec;
    private int _needInput;
    private bool _disposed;

    private MfH264Encoder(string name, IMFTransform transform, IMFDXGIDeviceManager manager, int fps)
    {
        Name = name;
        _transform = transform;
        _manager = manager;
        _fps = fps;
    }

    public string Name { get; }

    public byte[]? ParameterSets { get; private set; }

    /// <summary>Encoders de hardware H.264 de Media Foundation instalados (nombre del MFT).</summary>
    public static IReadOnlyList<string> HardwareEncoderNames()
    {
        EnsureStarted();
        var names = new List<string>();
        foreach (var activate in EnumerateHardware())
        {
            using (activate) names.Add(activate.GetAllocatedString(FriendlyNameKey));
        }

        return names;
    }

    /// <summary>
    /// Crea el encoder de hardware del fabricante de la GPU (por nombre: los activadores no informan
    /// el adaptador). <paramref name="vendorHint"/> es parte del nombre del MFT ("Intel", "AMD").
    /// </summary>
    public static MfH264Encoder Create(
        ID3D11Device device, string vendorHint, int width, int height, int fps, int bitrateMbps)
    {
        EnsureStarted();
        foreach (var activate in EnumerateHardware())
        {
            using (activate)
            {
                var name = activate.GetAllocatedString(FriendlyNameKey);
                if (!name.Contains(vendorHint, StringComparison.OrdinalIgnoreCase)) continue;

                var result = activate.ActivateObject<IMFTransform>(typeof(IMFTransform).GUID, out var transform);
                if (result.Failure || transform is null) continue;

                var manager = MediaFactory.MFCreateDXGIDeviceManager();
                var encoder = new MfH264Encoder(name, transform, manager, fps);
                try
                {
                    encoder.Open(device, width, height, bitrateMbps);
                    return encoder;
                }
                catch
                {
                    encoder.Dispose();
                    throw;
                }
            }
        }

        throw new InvalidOperationException($"No hay un encoder H.264 de hardware de Media Foundation para \"{vendorHint}\"");
    }

    private static IEnumerable<IMFActivate> EnumerateHardware() =>
        MediaFactory.MFTEnumEx(
            VideoEncoderCategory,
            MftEnumHardware | MftEnumSortAndFilter,
            null,
            new RegisterTypeInfo { GuidMajorType = VideoMajor, GuidSubtype = H264 });

    private static void EnsureStarted()
    {
        lock (StartupLock)
        {
            if (_started) return;
            MediaFactory.MFStartup(false).CheckError();
            _started = true;
        }
    }

    private void Open(ID3D11Device device, int width, int height, int bitrateMbps)
    {
        var transform = _transform!;
        using (var attributes = transform.Attributes)
        {
            attributes.Set(AsyncUnlockKey, 1u);
            attributes.Set(LowLatencyKey, 1u);
        }

        _manager.ResetDevice(device);
        transform.ProcessMessage(TMessageType.MessageSetD3DManager, (nuint)(nint)_manager.NativePointer);

        // Primero la salida (H.264) y después la entrada (NV12): así lo exigen los encoders.
        using (var output = MediaFactory.MFCreateMediaType())
        {
            output.Set(MajorTypeKey, VideoMajor);
            output.Set(SubtypeKey, H264);
            output.Set(AvgBitrateKey, (uint)(bitrateMbps * 1_000_000));
            output.Set(FrameSizeKey, Pack(width, height));
            output.Set(FrameRateKey, Pack(_fps, 1));
            output.Set(InterlaceKey, 2u); // progresivo
            output.Set(ProfileKey, 77u); // Main
            SetBt709(output);
            transform.SetOutputType(0, output, 0);
        }

        using (var input = MediaFactory.MFCreateMediaType())
        {
            input.Set(MajorTypeKey, VideoMajor);
            input.Set(SubtypeKey, Nv12);
            input.Set(FrameSizeKey, Pack(width, height));
            input.Set(FrameRateKey, Pack(_fps, 1));
            input.Set(InterlaceKey, 2u);
            SetBt709(input);
            transform.SetInputType(0, input, 0);
        }

        var iid = typeof(ICodecApi).GUID;
        if (Marshal.QueryInterface(transform.NativePointer, in iid, out var codecPointer) == 0)
        {
            _codec = (ICodecApi)Marshal.GetObjectForIUnknown(codecPointer);
            Marshal.Release(codecPointer);
            SetCodec(CodecRateControlMode, 0u); // CBR
            SetCodec(CodecMeanBitRate, (uint)(bitrateMbps * 1_000_000));
            SetCodec(CodecLowLatency, true);
            SetCodec(CodecBFrameCount, 0u);
            SetCodec(CodecGopSize, (uint)(_fps * 2));
        }

        _generator = transform.QueryInterface<IMFMediaEventGenerator>();
        _callback = new EventCallback(_generator, _events);
        _generator.BeginGetEvent(_callback, null);

        transform.ProcessMessage(TMessageType.MessageCommandFlush, 0);
        transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
        transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, 0);
    }

    /// <summary>BT.709 de rango limitado: lo que produce el conversor BGRA→NV12 y lo que la tablet debe usar al decodificar.</summary>
    private static void SetBt709(IMFMediaType type)
    {
        type.Set(YuvMatrixKey, 2u); // MFVideoTransferMatrix_BT709
        type.Set(NominalRangeKey, 2u); // MFNominalRange_16_235
        type.Set(PrimariesKey, 2u); // MFVideoPrimaries_BT709
        type.Set(TransferKey, 5u); // MFVideoTransFunc_709
    }

    private static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;

    private void SetCodec(string api, object value)
    {
        if (_codec is null) return;
        var guid = new Guid(api);
        // Los parámetros opcionales que el encoder no soporta no son un error.
        _ = _codec.SetValue(ref guid, ref value);
    }

    public void SetBitrate(int mbps) => SetCodec(CodecMeanBitRate, (uint)(mbps * 1_000_000));

    public void Encode(ID3D11Texture2D nv12, long ptsUs, bool forceKeyFrame, List<EncodedFrame> output)
    {
        if (forceKeyFrame) SetCodec(CodecForceKeyFrame, 1u);

        Pump(() => _needInput > 0, NeedInputTimeout, output);
        if (_needInput == 0) throw new IOException("El encoder de Media Foundation no pidió más cuadros");
        _needInput--;

        using (var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, nv12, 0, false))
        using (var sample = MediaFactory.MFCreateSample())
        {
            buffer.CurrentLength = buffer.MaxLength;
            sample.AddBuffer(buffer);
            sample.SampleTime = ptsUs * 10;
            sample.SampleDuration = 10_000_000L / _fps;
            _transform!.ProcessInput(0, sample, 0);
        }

        // La salida de este cuadro suele llegar enseguida; si no, se recoge en la próxima llamada.
        var before = output.Count;
        Pump(() => output.Count > before, TimeSpan.FromMilliseconds(40), output);
    }

    /// <summary>Procesa eventos del encoder hasta que se cumpla <paramref name="done"/> o pase el tiempo.</summary>
    private void Pump(Func<bool> done, TimeSpan timeout, List<EncodedFrame> output)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            var remaining = timeout - clock.Elapsed;
            if (remaining <= TimeSpan.Zero || !_events.TryTake(out var ev, remaining)) return;

            using (ev)
            {
                switch ((int)ev.EventType)
                {
                    case EventNeedInput:
                        _needInput++;
                        break;
                    case EventHaveOutput:
                        ReadOutput(output);
                        break;
                }
            }
        }
    }

    private void ReadOutput(List<EncodedFrame> output)
    {
        var buffer = new OutputDataBuffer { StreamID = 0 };
        var result = _transform!.ProcessOutput(ProcessOutputFlags.None, 1, ref buffer, out _);
        if (result.Failure || buffer.Sample is null) return;

        using var sample = buffer.Sample;
        using var contiguous = sample.ConvertToContiguousBuffer();
        contiguous.Lock(out var pointer, out _, out var length);
        byte[] data;
        try
        {
            data = new byte[length];
            Marshal.Copy(pointer, data, 0, length);
        }
        finally
        {
            contiguous.Unlock();
        }

        var keyFrame = AnnexB.ContainsIdr(data);
        if (keyFrame) ParameterSets ??= AnnexB.ExtractParameterSets(data);
        output.Add(new EncodedFrame(data, sample.SampleTime / 10, keyFrame ? FrameFlags.KeyFrame : FrameFlags.None));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _transform?.ProcessMessage(TMessageType.MessageNotifyEndStreaming, 0);
        }
        catch (SharpGenException)
        {
            // El encoder ya estaba roto: seguir liberando.
        }

        _callback?.Stop();
        _generator?.Dispose();
        if (_codec is not null) Marshal.ReleaseComObject(_codec);
        _transform?.Dispose();
        _manager.Dispose();
        _events.Dispose();
    }

    /// <summary>Recibe cada evento del MFT y lo deja en la cola, pidiendo el siguiente.</summary>
    private sealed class EventCallback(IMFMediaEventGenerator generator, BlockingCollection<IMFMediaEvent> queue)
        : CallbackBase, IMFAsyncCallback
    {
        private volatile bool _stopped;

        public void Stop() => _stopped = true;

        public Result GetParameters(out AsyncCallbackFlags flags, out int queueId)
        {
            flags = AsyncCallbackFlags.None;
            queueId = 0; // MFASYNC_CALLBACK_QUEUE_STANDARD
            return Result.Ok;
        }

        public Result Invoke(IMFAsyncResult asyncResult)
        {
            if (_stopped) return Result.Ok;
            try
            {
                var ev = generator.EndGetEvent(asyncResult);
                if (!queue.IsAddingCompleted) queue.Add(ev);
                if (!_stopped) generator.BeginGetEvent(this, null);
            }
            catch (Exception ex) when (ex is SharpGenException or InvalidOperationException or ObjectDisposedException)
            {
                // El encoder se está cerrando.
            }

            return Result.Ok;
        }
    }

    [ComImport, Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICodecApi
    {
        [PreserveSig] int IsSupported(ref Guid api);
        [PreserveSig] int IsModifiable(ref Guid api);
        [PreserveSig] int GetParameterRange(ref Guid api, out object min, out object max, out object step);
        [PreserveSig] int GetParameterValues(ref Guid api, out IntPtr values, out uint count);
        [PreserveSig] int GetDefaultValue(ref Guid api, out object value);
        [PreserveSig] int GetValue(ref Guid api, out object value);
        [PreserveSig] int SetValue(ref Guid api, ref object value);
    }
}
