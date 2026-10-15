using NexScreen.Core.Display;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace NexScreen.Core.Video.Native;

internal enum AcquireStatus
{
    /// <summary>La pantalla cambió: hay una imagen nueva en <see cref="DesktopCapture.Convert"/>.</summary>
    Updated,

    /// <summary>Pasó el tiempo sin cambios.</summary>
    Timeout,
}

/// <summary>Windows invalidó Desktop Duplication (cambio de resolución, UAC, sesión bloqueada...). Hay que reiniciar la captura.</summary>
internal sealed class CaptureLostException(string message) : Exception(message);

/// <summary>
/// Captura una pantalla con Desktop Duplication directo (sin ffmpeg) y la deja en NV12 en la GPU:
/// copia cada imagen nueva a una textura propia y la convierte (y escala si hace falta) con el
/// VideoProcessor de D3D11. Se usa desde un solo hilo.
/// </summary>
internal sealed class DesktopCapture : IDisposable
{
    private const int PoolSize = 4;

    private readonly IDXGIOutputDuplication _duplication;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly ID3D11VideoProcessorEnumerator _enumerator;
    private readonly ID3D11VideoProcessor _processor;
    private readonly ID3D11Texture2D _latest;
    private readonly ID3D11VideoProcessorInputView _inputView;
    private readonly ID3D11Texture2D[] _pool = new ID3D11Texture2D[PoolSize];
    private readonly ID3D11VideoProcessorOutputView[] _poolViews = new ID3D11VideoProcessorOutputView[PoolSize];
    private int _next;
    private bool _disposed;

    // Cursor: la duplicación entrega el escritorio sin él, así que se dibuja como segunda capa.
    private bool _pointerVisible;
    private int _pointerX;
    private int _pointerY;
    private int _hotspotX;
    private int _hotspotY;
    private int _cursorWidth;
    private int _cursorHeight;
    private ID3D11Texture2D? _cursorTexture;
    private ID3D11VideoProcessorInputView? _cursorView;

    private DesktopCapture(
        ID3D11Device device, ID3D11DeviceContext context, IDXGIOutputDuplication duplication,
        int captureWidth, int captureHeight, int outWidth, int outHeight)
    {
        Device = device;
        Context = context;
        _duplication = duplication;
        CaptureWidth = captureWidth;
        CaptureHeight = captureHeight;
        OutWidth = outWidth;
        OutHeight = outHeight;

        _videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = context.QueryInterface<ID3D11VideoContext>();

        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)captureWidth,
            InputHeight = (uint)captureHeight,
            OutputWidth = (uint)outWidth,
            OutputHeight = (uint)outHeight,
            Usage = VideoUsage.PlaybackNormal,
        };
        _enumerator = _videoDevice.CreateVideoProcessorEnumerator(content);
        _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);
        _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
        _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 1, VideoFrameFormat.Progressive);
        _videoContext.VideoProcessorSetStreamDestRect(_processor, 0, true, new RawRect(0, 0, outWidth, outHeight));

        // RGB de rango completo → YCbCr BT.709 de rango limitado, lo que declara el stream (VUI) y espera la tablet.
        _videoContext.VideoProcessorSetStreamColorSpace(_processor, 0, new VideoProcessorColorSpace { RGB_Range = 0, YCbCr_Matrix = 1 });
        _videoContext.VideoProcessorSetOutputColorSpace(_processor, new VideoProcessorColorSpace { YCbCr_Matrix = 1, Nominal_Range = 1 });

        _latest = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)captureWidth,
            Height = (uint)captureHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
        });
        _inputView = _videoDevice.CreateVideoProcessorInputView(_latest, _enumerator, new VideoProcessorInputViewDescription
        {
            FourCC = 0,
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
        });

        for (var i = 0; i < PoolSize; i++)
        {
            _pool[i] = device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)outWidth,
                Height = (uint)outHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                // Solo RenderTarget (lo que usa el VideoProcessor para escribir): NVENC rechaza el mapeo con otros flags.
                BindFlags = BindFlags.RenderTarget,
            });
            _poolViews[i] = _videoDevice.CreateVideoProcessorOutputView(_pool[i], _enumerator, new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
            });
        }
    }

    public ID3D11Device Device { get; }

    public ID3D11DeviceContext Context { get; }

    public int CaptureWidth { get; }

    public int CaptureHeight { get; }

    public int OutWidth { get; }

    public int OutHeight { get; }

    /// <summary>Abre la duplicación de la pantalla con un dispositivo D3D11 en su adaptador.</summary>
    public static DesktopCapture Create(CaptureTarget target, int maxWidth, int maxHeight)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        if (!factory.EnumAdapters1((uint)target.Adapter, out var adapter).Success)
        {
            throw new InvalidOperationException($"No existe el adaptador gráfico {target.Adapter}");
        }

        using (adapter)
        {
            if (!adapter.EnumOutputs((uint)target.Output, out var output).Success)
            {
                throw new InvalidOperationException($"El adaptador {target.Adapter} no tiene la salida {target.Output}");
            }

            using (output)
            {
                D3D11.D3D11CreateDevice(
                    adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                    null, out ID3D11Device? device, out ID3D11DeviceContext? context).CheckError();
                using (var multithread = device!.QueryInterface<ID3D11Multithread>()) multithread.SetMultithreadProtected(true);

                IDXGIOutputDuplication duplication;
                try
                {
                    using var output1 = output.QueryInterface<IDXGIOutput1>();
                    duplication = output1.DuplicateOutput(device);
                }
                catch
                {
                    context!.Dispose();
                    device.Dispose();
                    throw;
                }

                var mode = duplication.Description.ModeDescription;
                var (outWidth, outHeight) = StreamTuning.FitSize((int)mode.Width, (int)mode.Height, maxWidth, maxHeight);
                return new DesktopCapture(device, context!, duplication, (int)mode.Width, (int)mode.Height, outWidth, outHeight);
            }
        }
    }

    /// <summary>
    /// Espera hasta <paramref name="timeoutMs"/> ms una imagen nueva. Si la hay, queda copiada para
    /// <see cref="Convert"/>. Lanza <see cref="CaptureLostException"/> si Windows invalidó la captura.
    /// </summary>
    public AcquireStatus Acquire(int timeoutMs)
    {
        var result = _duplication.AcquireNextFrame((uint)Math.Max(0, timeoutMs), out var info, out var resource);
        if (result == Vortice.DXGI.ResultCode.WaitTimeout) return AcquireStatus.Timeout;
        if (result == Vortice.DXGI.ResultCode.AccessLost) throw new CaptureLostException($"Desktop Duplication perdió el acceso ({result.Code:x8})");
        if (result.Failure) throw new CaptureLostException($"Desktop Duplication falló ({result.Code:x8})");

        try
        {
            var changed = false;
            if (info.LastMouseUpdateTime != 0)
            {
                _pointerVisible = info.PointerPosition.Visible;
                _pointerX = info.PointerPosition.Position.X;
                _pointerY = info.PointerPosition.Position.Y;
                changed = true;
            }

            if (info.PointerShapeBufferSize > 0)
            {
                UpdateCursorShape((int)info.PointerShapeBufferSize);
                changed = true;
            }

            // LastPresentTime en 0: la imagen del escritorio es la misma (solo cambió el cursor).
            if (info.LastPresentTime != 0)
            {
                using var texture = resource!.QueryInterface<ID3D11Texture2D>();
                Context.CopyResource(_latest, texture);
                changed = true;
            }

            return changed ? AcquireStatus.Updated : AcquireStatus.Timeout;
        }
        finally
        {
            resource?.Dispose();
            _duplication.ReleaseFrame();
        }
    }

    private unsafe void UpdateCursorShape(int bufferSize)
    {
        var buffer = new byte[bufferSize];
        OutduplPointerShapeInfo shape;
        fixed (byte* pointer = buffer)
        {
            _duplication.GetFramePointerShape((uint)bufferSize, (nint)pointer, out _, out shape);
        }

        var (width, height, bgra) = CursorImage.ToBgra((int)shape.Type, (int)shape.Width, (int)shape.Height, (int)shape.Pitch, buffer);
        _cursorView?.Dispose();
        _cursorTexture?.Dispose();
        _cursorView = null;
        _cursorTexture = null;
        if (width == 0 || height == 0) return;

        fixed (byte* pixels = bgra)
        {
            _cursorTexture = Device.CreateTexture2D(
                new Texture2DDescription
                {
                    Width = (uint)width,
                    Height = (uint)height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                },
                [new SubresourceData((nint)pixels, (uint)(width * 4))]);
        }

        _cursorView = _videoDevice.CreateVideoProcessorInputView(_cursorTexture, _enumerator, new VideoProcessorInputViewDescription
        {
            FourCC = 0,
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
        });
        _cursorWidth = width;
        _cursorHeight = height;
        _hotspotX = shape.HotSpot.X;
        _hotspotY = shape.HotSpot.Y;
    }

    /// <summary>Convierte la última imagen a NV12 en la próxima textura del pool y la devuelve (válida hasta 3 llamadas después).</summary>
    public ID3D11Texture2D Convert()
    {
        var index = _next;
        _next = (_next + 1) % PoolSize;

        var desktop = new VideoProcessorStream { Enable = true, InputSurface = _inputView };
        if (TryPlaceCursor() is { } cursorView)
        {
            var cursor = new VideoProcessorStream { Enable = true, InputSurface = cursorView };
            _videoContext.VideoProcessorBlt(_processor, _poolViews[index], 0, 2, [desktop, cursor]);
        }
        else
        {
            _videoContext.VideoProcessorBlt(_processor, _poolViews[index], 0, 1, [desktop]);
        }

        Context.Flush();
        return _pool[index];
    }

    /// <summary>Ubica el cursor (escalado al tamaño de salida y recortado a los bordes). Null si no se ve.</summary>
    private ID3D11VideoProcessorInputView? TryPlaceCursor()
    {
        if (!_pointerVisible || _cursorView is null) return null;

        var scaleX = (double)OutWidth / CaptureWidth;
        var scaleY = (double)OutHeight / CaptureHeight;
        var left = (_pointerX - _hotspotX) * scaleX;
        var top = (_pointerY - _hotspotY) * scaleY;
        var right = left + _cursorWidth * scaleX;
        var bottom = top + _cursorHeight * scaleY;

        var clipLeft = Math.Max(0, left);
        var clipTop = Math.Max(0, top);
        var clipRight = Math.Min(OutWidth, right);
        var clipBottom = Math.Min(OutHeight, bottom);
        if (clipRight - clipLeft < 1 || clipBottom - clipTop < 1) return null;

        // La parte visible del cursor, en píxeles de su propia imagen.
        var srcLeft = (int)Math.Round((clipLeft - left) / scaleX);
        var srcTop = (int)Math.Round((clipTop - top) / scaleY);
        var srcRight = Math.Min(_cursorWidth, (int)Math.Round((clipRight - left) / scaleX));
        var srcBottom = Math.Min(_cursorHeight, (int)Math.Round((clipBottom - top) / scaleY));
        if (srcRight <= srcLeft || srcBottom <= srcTop) return null;

        _videoContext.VideoProcessorSetStreamSourceRect(_processor, 1, true, new RawRect(srcLeft, srcTop, srcRight, srcBottom));
        _videoContext.VideoProcessorSetStreamDestRect(
            _processor, 1, true, new RawRect((int)Math.Round(clipLeft), (int)Math.Round(clipTop), (int)Math.Round(clipRight), (int)Math.Round(clipBottom)));
        _videoContext.VideoProcessorSetStreamAlpha(_processor, 1, true, 1f);
        return _cursorView;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cursorView?.Dispose();
        _cursorTexture?.Dispose();
        foreach (var view in _poolViews) view?.Dispose();
        foreach (var texture in _pool) texture?.Dispose();
        _inputView.Dispose();
        _latest.Dispose();
        _processor.Dispose();
        _enumerator.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
        _duplication.Dispose();
        Context.Dispose();
        Device.Dispose();
    }
}
