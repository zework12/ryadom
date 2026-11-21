using System.Diagnostics;
using System.Runtime.InteropServices;
using Ryadom.Live;
using Ryadom.Shared;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Ryadom.Host;

/// <summary>
/// Захват монитора через Windows.Graphics.Capture. Кадр не выходит из
/// видеокарты, пока она сама не уменьшит его до нужного размера и не
/// переведёт в NV12 (формат кодека) — в память копируется в 2,7 раза меньше,
/// чем BGRA, и процессор не тратится на перевод цветов. Если видеокарта так
/// не умеет — запасной путь: BGRA как есть.
/// </summary>
public sealed unsafe class ScreenSource : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDirect3DDevice _winrtDevice;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Lock _gate = new();
    private readonly Timer _repeat;
    private readonly long _minGap;
    private Windows.Graphics.SizeInt32 _poolSize;

    // Последний кадр на видеокарте (копия кадра захвата) — для повторов.
    private ID3D11Texture2D? _copy;
    private int _copyWidth, _copyHeight;
    private bool _hasFrame;

    // Видеопроцессор: BGRA любого размера → NV12 размера Width×Height.
    private ID3D11VideoDevice? _videoDevice;
    private ID3D11VideoContext? _videoContext;
    private ID3D11VideoProcessorEnumerator? _vpEnum;
    private ID3D11VideoProcessor? _vp;
    private ID3D11VideoProcessorInputView? _vpInput;
    private ID3D11VideoProcessorOutputView? _vpOutput;
    private ID3D11Texture2D? _nv12;
    private ID3D11Texture2D? _staging;
    private int _vpInWidth, _vpInHeight;
    private bool _gpuConvert = true;

    private long _lastPush = long.MinValue / 2;
    private bool _disposed;
    private int _errors;

    /// <summary>Куда отдавать кадры; null — снимать, но не слать.</summary>
    public ScreenTrack? Target { get; set; }

    /// <summary>Размер кадров, которые уходят в сеть (чётный).</summary>
    public int Width { get; }
    public int Height { get; }

    /// <summary>Сколько кадров ушло — для счётчика.</summary>
    public int Pushed;

    public event Action? Ended;

    public static bool IsSupported => GraphicsCaptureSession.IsSupported();

    /// <param name="maxHeight">Наибольшая высота кадра в сети; 0 — как на экране.</param>
    public ScreenSource(MonitorDesc monitor, int fps, int maxHeight)
    {
        _minGap = Stopwatch.Frequency / Math.Max(1, fps) * 9 / 10;
        var created = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1], out ID3D11Device device, out ID3D11DeviceContext context);
        if (created.Failure)
        {
            // Без видеоподдержки (старый драйвер) — без неё, кадры пойдут в BGRA.
            D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1], out device, out context).CheckError();
            _gpuConvert = false;
        }
        _device = device!;
        _context = context!;
        // Захват идёт в своём потоке, а контекст D3D — один: защищаем его.
        using (var mt = _device.QueryInterfaceOrNull<ID3D11Multithread>()) mt?.SetMultithreadProtected(true);
        using (var dxgi = _device.QueryInterface<IDXGIDevice>())
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var inspectable));
            _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            Marshal.Release(inspectable);
        }

        var b = monitor.Bounds;
        (Width, Height) = FitSize(b.Width, b.Height, maxHeight);

        _item = CreateItemForMonitor(monitor.Handle);
        _item.Closed += (_, _) => Ended?.Invoke();
        _poolSize = _item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(_item);
        // Курсор не рисуем в картинку: он идёт отдельно (CursorWatcher) и у помощника без задержки.
        _session.IsCursorCaptureEnabled = false;
        if (Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
        {
            try { _session.IsBorderRequired = false; } catch (Exception) { }
        }
        _session.StartCapture();
        // Неподвижный экран всё равно повторяем: кодек за это время дорисует
        // мелкий текст до чёткого, а только что подключившийся сразу увидит картинку.
        _repeat = new Timer(_ => Repeat(), null, 250, 250);
        Log.Info($"Захват {monitor.Title} {b.Width}×{b.Height} → {Width}×{Height}, {fps} к/с, видеокарта: {(_gpuConvert ? "NV12" : "нет")}");
    }

    /// <summary>Уменьшить до maxHeight с сохранением пропорций; стороны чётные.</summary>
    public static (int Width, int Height) FitSize(int width, int height, int maxHeight)
    {
        if (maxHeight > 0 && height > maxHeight)
        {
            width = (int)Math.Round(width * (double)maxHeight / height);
            height = maxHeight;
        }
        return (Math.Max(2, width & ~1), Math.Max(2, height & ~1));
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool pool, object _)
    {
        try { TakeFrame(pool); }
        catch (Exception error) when (_errors++ < 5) { Log.Error("Кадр захвата", error); }
    }

    private void TakeFrame(Direct3D11CaptureFramePool pool)
    {
        using var frame = pool.TryGetNextFrame();
        if (frame is null) return;
        lock (_gate)
        {
            if (_disposed) return;
            var size = frame.ContentSize;
            if (size.Width != _poolSize.Width || size.Height != _poolSize.Height)
            {
                // Сменилось разрешение экрана — пул под новый размер.
                _poolSize = size;
                _pool.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
                return;
            }
            var now = _clock.ElapsedTicks;
            if (now - _lastPush < _minGap) return;

            var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
            var iid = typeof(ID3D11Texture2D).GUID;
            using var texture = new ID3D11Texture2D(access.GetInterface(ref iid));
            var desc = texture.Description;
            var width = Math.Min(size.Width, (int)desc.Width);
            var height = Math.Min(size.Height, (int)desc.Height);
            if (width < 2 || height < 2) return;
            if (_copy is null || _copyWidth != (int)desc.Width || _copyHeight != (int)desc.Height)
            {
                _copy?.Dispose();
                _copy = _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = desc.Width,
                    Height = desc.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                });
                _copyWidth = (int)desc.Width;
                _copyHeight = (int)desc.Height;
                ResetProcessor();
            }
            _context.CopyResource(_copy, texture);
            _hasFrame = true;
            Push(now);
        }
    }

    private void Repeat()
    {
        lock (_gate)
        {
            if (_disposed || !_hasFrame) return;
            var now = _clock.ElapsedTicks;
            if (now - _lastPush >= Stopwatch.Frequency / 4)
            {
                try { Push(now); }
                catch (Exception error) when (_errors++ < 5) { Log.Error("Повтор кадра", error); }
            }
        }
    }

    private void Push(long now)
    {
        _lastPush = now;
        var target = Target;
        if (target is null || _copy is null) return;
        var timestamp = now * 1_000_000 / Stopwatch.Frequency;
        if (_gpuConvert)
        {
            try
            {
                PushNv12(target, timestamp);
                Interlocked.Increment(ref Pushed);
                return;
            }
            catch (Exception error)
            {
                Log.Error("Видеокарта не перевела кадр в NV12 — дальше BGRA", error);
                _gpuConvert = false;
                ResetProcessor();
            }
        }
        PushBgra(target, timestamp);
        Interlocked.Increment(ref Pushed);
    }

    // ── Путь через видеокарту ──────────────────────────────────

    private void EnsureProcessor()
    {
        if (_vp is not null && _vpInWidth == _copyWidth && _vpInHeight == _copyHeight) return;
        ResetProcessor();
        _videoDevice ??= _device.QueryInterface<ID3D11VideoDevice>();
        _videoContext ??= _context.QueryInterface<ID3D11VideoContext>();
        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)_copyWidth,
            InputHeight = (uint)_copyHeight,
            OutputWidth = (uint)Width,
            OutputHeight = (uint)Height,
            InputFrameRate = new Rational(60, 1),
            OutputFrameRate = new Rational(60, 1),
            Usage = VideoUsage.OptimalSpeed,
        };
        _vpEnum = _videoDevice.CreateVideoProcessorEnumerator(content);
        _vp = _videoDevice.CreateVideoProcessor(_vpEnum, 0);
        _nv12 = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
        });
        _staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
        });
        _vpInput = _videoDevice.CreateVideoProcessorInputView(_copy!, _vpEnum, new VideoProcessorInputViewDescription
        {
            FourCC = 0,
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
        });
        _vpOutput = _videoDevice.CreateVideoProcessorOutputView(_nv12, _vpEnum, new VideoProcessorOutputViewDescription
        {
            ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
        });
        // Цвета: на входе обычный RGB 0–255, на выходе BT.601 16–235 —
        // так их понимает libyuv на стороне помощника.
        using (var ctx1 = _videoContext.QueryInterfaceOrNull<ID3D11VideoContext1>())
        {
            if (ctx1 is not null)
            {
                ctx1.VideoProcessorSetStreamColorSpace1(_vp, 0, ColorSpaceType.RgbFullG22NoneP709);
                ctx1.VideoProcessorSetOutputColorSpace1(_vp, ColorSpaceType.YcbcrStudioG22LeftP601);
            }
        }
        _videoContext.VideoProcessorSetStreamFrameFormat(_vp, 0, VideoFrameFormat.Progressive);
        _videoContext.VideoProcessorSetStreamAutoProcessingMode(_vp, 0, false);
        _vpInWidth = _copyWidth;
        _vpInHeight = _copyHeight;
    }

    private void PushNv12(ScreenTrack target, long timestamp)
    {
        EnsureProcessor();
        var streams = new[]
        {
            new VideoProcessorStream { Enable = true, InputSurface = _vpInput! },
        };
        _videoContext!.VideoProcessorBlt(_vp!, _vpOutput!, 0, 1, streams).CheckError();
        _context.CopyResource(_staging!, _nv12!);
        var mapped = _context.Map(_staging!, 0, MapMode.Read);
        try
        {
            var y = (byte*)mapped.DataPointer;
            var uv = y + (long)mapped.RowPitch * Height;
            target.PushNv12(y, (int)mapped.RowPitch, uv, (int)mapped.RowPitch, Width, Height, timestamp);
        }
        finally
        {
            _context.Unmap(_staging!, 0);
        }
    }

    private void ResetProcessor()
    {
        _vpInput?.Dispose(); _vpInput = null;
        _vpOutput?.Dispose(); _vpOutput = null;
        _vp?.Dispose(); _vp = null;
        _vpEnum?.Dispose(); _vpEnum = null;
        _nv12?.Dispose(); _nv12 = null;
        if (_staging is not null && _staging.Description.Format == Format.NV12) { _staging.Dispose(); _staging = null; }
        _vpInWidth = _vpInHeight = 0;
    }

    // ── Запасной путь: BGRA через процессор ────────────────────

    private void PushBgra(ScreenTrack target, long timestamp)
    {
        if (_staging is null || _staging.Description.Format != Format.B8G8R8A8_UNorm
            || _staging.Description.Width != _copyWidth || _staging.Description.Height != _copyHeight)
        {
            _staging?.Dispose();
            _staging = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_copyWidth,
                Height = (uint)_copyHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                CPUAccessFlags = CpuAccessFlags.Read,
            });
        }
        _context.CopyResource(_staging, _copy!);
        var mapped = _context.Map(_staging, 0, MapMode.Read);
        try
        {
            target.PushBgra((byte*)mapped.DataPointer, _copyWidth & ~1, _copyHeight & ~1, (int)mapped.RowPitch, timestamp);
        }
        finally
        {
            _context.Unmap(_staging, 0);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _repeat.Dispose();
        _session.Dispose();
        _pool.Dispose();
        lock (_gate)
        {
            ResetProcessor();
            _staging?.Dispose();
            _copy?.Dispose();
            _videoContext?.Dispose();
            _videoDevice?.Dispose();
            _context.Dispose();
            _device.Dispose();
        }
    }

    private static GraphicsCaptureItem CreateItemForMonitor(IntPtr monitor)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        var pointer = interop.CreateForMonitor(monitor, ref iid);
        try { return GraphicsCaptureItem.FromAbi(pointer); }
        finally { Marshal.Release(pointer); }
    }

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, ref Guid iid);
        IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid);
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface(ref Guid iid);
    }

    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
}
