using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Ryadom.Shared;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Ryadom.Viewer;

/// <summary>
/// Картинка чужого экрана у помощника: своё окно Direct3D внутри WPF.
/// Кадры рисуются сразу, как пришли, в своём потоке — без очереди WPF,
/// так задержка меньше на кадр-два. Мышь и колёсико ловим прямо в окне,
/// клавиши — через KeyboardGrab (иначе Win, Alt+Tab ушли бы своей системе).
/// </summary>
public sealed unsafe class VideoView : HwndHost
{
    private IntPtr _hwnd;
    private Thread? _thread;
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _stop;
    private readonly WndProcDelegate _proc;

    // Последний пришедший кадр (из потока движка) — рисующий забирает его.
    private readonly Lock _frameGate = new();
    private byte[] _frame = [];
    private int _frameWidth, _frameHeight;
    private bool _frameFresh;
    private volatile bool _resized = true;

    // Где на окне сейчас картинка (для перевода координат мыши).
    private int _viewX, _viewY, _viewW, _viewH;
    private int _lastButtonsDown;

    /// <summary>Пакет ввода для отправки (мышь). Из потока окна.</summary>
    public event Action<byte[], bool>? Input;

    /// <summary>Окно картинки получило (true) или потеряло (false) фокус клавиатуры.</summary>
    public event Action<bool>? FocusChanged;

    /// <summary>Первый кадр нарисован.</summary>
    public event Action? FirstFrame;

    public bool ControlEnabled { get; set; } = true;

    // ── Курсор человека ────────────────────────────────────────
    // Форма приходит один раз (номер → картинка), место — постоянно.
    // Пока мышь помощника над картинкой и управление включено, курсор —
    // его собственный системный, только той же формы, что у человека
    // (без задержки). Иначе курсор человека рисуется поверх картинки.

    private sealed record CursorShape(ushort Id, int Width, int Height, int HotX, int HotY, byte[] Bgra)
    {
        public IntPtr Handle;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<ushort, CursorShape> _shapes = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<CursorShape> _newShapes = new();
    private volatile bool _cursorVisible;
    private double _cursorX, _cursorY;
    private volatile ushort _cursorId;
    private volatile bool _mouseInside;

    /// <summary>Новая форма курсора человека (BGRA с прозрачностью).</summary>
    public void AddCursorShape(ushort id, int width, int height, int hotX, int hotY, byte[] bgra)
    {
        if (width <= 0 || height <= 0 || bgra.Length != width * height * 4) return;
        var shape = new CursorShape(id, width, height, hotX, hotY, bgra);
        _shapes[id] = shape;
        _newShapes.Enqueue(shape);
        if (id == _cursorId && _hwnd != IntPtr.Zero) PostMessage(_hwnd, WM_APP_CURSOR, IntPtr.Zero, IntPtr.Zero);
        _wake.Set();
    }

    /// <summary>Где курсор человека (доли его экрана) и какой он.</summary>
    public void SetCursor(double fx, double fy, bool visible, ushort id)
    {
        var changedShape = id != _cursorId || visible != _cursorVisible;
        _cursorX = fx;
        _cursorY = fy;
        _cursorId = id;
        _cursorVisible = visible;
        if (changedShape && _hwnd != IntPtr.Zero) PostMessage(_hwnd, WM_APP_CURSOR, IntPtr.Zero, IntPtr.Zero);
        if (!(_mouseInside && ControlEnabled)) _wake.Set();
    }

    /// <summary>Системный курсор той же формы, что у человека.</summary>
    private IntPtr RemoteCursorHandle()
    {
        var arrow = LoadCursor(IntPtr.Zero, 32512);
        if (!_shapes.TryGetValue(_cursorId, out var shape)) return arrow;
        if (shape.Handle == IntPtr.Zero) shape.Handle = MakeCursor(shape);
        return shape.Handle == IntPtr.Zero ? arrow : shape.Handle;
    }

    private void ApplyCursor()
    {
        if (!ControlEnabled) { SetCursorNative(LoadCursor(IntPtr.Zero, 32512)); return; }
        // Курсор человека спрятан (сенсорный экран, печатает) — у помощника обычная стрелка,
        // чтобы своя мышь не пропадала.
        SetCursorNative(_cursorVisible ? RemoteCursorHandle() : LoadCursor(IntPtr.Zero, 32512));
    }

    private static IntPtr MakeCursor(CursorShape shape)
    {
        var header = new BITMAPV5HEADER
        {
            Size = (uint)Marshal.SizeOf<BITMAPV5HEADER>(),
            Width = shape.Width,
            Height = -shape.Height,
            Planes = 1,
            BitCount = 32,
            Compression = 3, // BI_BITFIELDS
            RedMask = 0x00FF0000,
            GreenMask = 0x0000FF00,
            BlueMask = 0x000000FF,
            AlphaMask = 0xFF000000,
        };
        var dc = GetDC(IntPtr.Zero);
        var color = CreateDIBSection(dc, ref header, 0, out var bits, IntPtr.Zero, 0);
        ReleaseDC(IntPtr.Zero, dc);
        if (color == IntPtr.Zero) return IntPtr.Zero;
        Marshal.Copy(shape.Bgra, 0, bits, shape.Bgra.Length);
        var mask = CreateBitmap(shape.Width, shape.Height, 1, 1, IntPtr.Zero);
        var info = new ICONINFO { IsIcon = false, HotspotX = shape.HotX, HotspotY = shape.HotY, Mask = mask, Color = color };
        var cursor = CreateIconIndirect(ref info);
        DeleteObject(color);
        DeleteObject(mask);
        return cursor;
    }

    /// <summary>Нарисовано кадров с запуска.</summary>
    public int Rendered;

    public int SourceWidth => _frameWidth;

    /// <summary>Копия последнего кадра (BGRA) — для Claude. null — кадров ещё не было.</summary>
    public byte[]? Snapshot(out int width, out int height)
    {
        lock (_frameGate)
        {
            width = _frameWidth;
            height = _frameHeight;
            return _frame.Length == 0 ? null : (byte[])_frame.Clone();
        }
    }
    public int SourceHeight => _frameHeight;

    public IntPtr Hwnd => _hwnd;

    public VideoView()
    {
        _proc = WndProc;
        Focusable = true;
    }

    /// <summary>Кадр BGRA из сети. Копируется; рисует поток картинки.</summary>
    public void Push(byte* bgra, int width, int height, int stride)
    {
        lock (_frameGate)
        {
            var row = width * 4;
            if (_frame.Length != row * height) _frame = new byte[row * height];
            fixed (byte* target = _frame)
            {
                if (stride == row) Buffer.MemoryCopy(bgra, target, _frame.Length, _frame.Length);
                else for (var y = 0; y < height; y++) Buffer.MemoryCopy(bgra + (long)y * stride, target + (long)y * row, row, row);
            }
            _frameWidth = width;
            _frameHeight = height;
            _frameFresh = true;
        }
        _wake.Set();
    }

    // ── Окно ───────────────────────────────────────────────────

    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        var cls = "RyadomVideo";
        var wc = new WNDCLASSEX
        {
            Size = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            Style = 0x20, // CS_OWNDC
            WndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            Instance = GetModuleHandle(null),
            Cursor = LoadCursor(IntPtr.Zero, 32512),
            ClassName = cls,
        };
        RegisterClassEx(ref wc);
        _hwnd = CreateWindowEx(0, cls, "", WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS, 0, 0, 1, 1, parent.Handle, IntPtr.Zero, wc.Instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("Окно картинки не создалось");
        _stop = false;
        _thread = new Thread(RenderLoop) { IsBackground = true, Name = "Картинка", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        _stop = true;
        _wake.Set();
        _thread?.Join(2000);
        DestroyWindow(hwnd.Handle);
        _hwnd = IntPtr.Zero;
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_SIZE:
                _resized = true;
                _wake.Set();
                break;
            case WM_ERASEBKGND:
                return 1;
            case WM_SETCURSOR when (lParam.ToInt64() & 0xFFFF) == 1: // HTCLIENT
                ApplyCursor();
                return 1;
            case WM_APP_CURSOR:
                if (_mouseInside) ApplyCursor();
                return 0;
            case WM_MOUSELEAVE:
                _mouseInside = false;
                _wake.Set();
                return 0;
            case WM_SETFOCUS:
                FocusChanged?.Invoke(true);
                break;
            case WM_KILLFOCUS:
                FocusChanged?.Invoke(false);
                break;
            case WM_MOUSEACTIVATE:
                SetFocus(hwnd);
                return 1; // MA_ACTIVATE
            case WM_MOUSEMOVE:
                if (!_mouseInside)
                {
                    _mouseInside = true;
                    var track = new TRACKMOUSEEVENT { Size = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(), Flags = 2 /* TME_LEAVE */, Hwnd = hwnd };
                    TrackMouseEvent(ref track);
                    _wake.Set();
                }
                SendMove(lParam);
                return 0;
            case WM_LBUTTONDOWN or WM_LBUTTONDBLCLK: Button(hwnd, Protocol.Left, true, lParam); return 0;
            case WM_LBUTTONUP: Button(hwnd, Protocol.Left, false, lParam); return 0;
            case WM_RBUTTONDOWN or WM_RBUTTONDBLCLK: Button(hwnd, Protocol.Right, true, lParam); return 0;
            case WM_RBUTTONUP: Button(hwnd, Protocol.Right, false, lParam); return 0;
            case WM_MBUTTONDOWN or WM_MBUTTONDBLCLK: Button(hwnd, Protocol.Middle, true, lParam); return 0;
            case WM_MBUTTONUP: Button(hwnd, Protocol.Middle, false, lParam); return 0;
            case WM_XBUTTONDOWN or WM_XBUTTONDBLCLK:
                Button(hwnd, HiWord(wParam) == 1 ? Protocol.X1 : Protocol.X2, true, lParam);
                return 1;
            case WM_XBUTTONUP:
                Button(hwnd, HiWord(wParam) == 1 ? Protocol.X1 : Protocol.X2, false, lParam);
                return 1;
            case WM_MOUSEWHEEL:
                if (ControlEnabled) Input?.Invoke(Protocol.EncodeWheel((short)HiWord(wParam), false), true);
                return 0;
            case WM_MOUSEHWHEEL:
                if (ControlEnabled) Input?.Invoke(Protocol.EncodeWheel((short)HiWord(wParam), true), true);
                return 0;
            case WM_CAPTURECHANGED:
                if (_lastButtonsDown != 0 && ControlEnabled) Input?.Invoke(Protocol.EncodeReleaseAll(), true);
                _lastButtonsDown = 0;
                break;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void SendMove(IntPtr lParam)
    {
        if (!ControlEnabled || _viewW <= 0 || _viewH <= 0) return;
        var x = (short)(lParam.ToInt64() & 0xFFFF);
        var y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        var fx = (x - _viewX) / (double)Math.Max(1, _viewW - 1);
        var fy = (y - _viewY) / (double)Math.Max(1, _viewH - 1);
        // Вне картинки (поля по бокам) — не двигаем: мышь упрётся в край.
        Input?.Invoke(Protocol.EncodeMove(fx, fy), false);
    }

    private void Button(IntPtr hwnd, byte button, bool down, IntPtr lParam)
    {
        if (down)
        {
            SetFocus(hwnd);
            if (_lastButtonsDown++ == 0) SetCapture(hwnd);
        }
        else if (_lastButtonsDown > 0 && --_lastButtonsDown == 0)
        {
            ReleaseCapture();
        }
        if (!ControlEnabled) return;
        // Сначала точное место, потом нажатие — иначе клик мог уйти по старой точке.
        SendMove(lParam);
        Input?.Invoke(Protocol.EncodeButton(button, down), true);
    }

    private static int HiWord(IntPtr value) => (int)((value.ToInt64() >> 16) & 0xFFFF);

    // ── Рисование ──────────────────────────────────────────────

    private const string Shader = """
        Texture2D tex : register(t0);
        SamplerState samp : register(s0);
        struct V { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
        V vs(uint id : SV_VertexID)
        {
            V o;
            o.uv = float2((id << 1) & 2, id & 2);
            o.pos = float4(o.uv * float2(2, -2) + float2(-1, 1), 0, 1);
            return o;
        }
        float4 ps(V i) : SV_Target { return float4(tex.Sample(samp, i.uv).rgb, 1); }
        float4 psa(V i) : SV_Target { return tex.Sample(samp, i.uv); }
        """;

    private void RenderLoop()
    {
        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        IDXGISwapChain1? swap = null;
        ID3D11RenderTargetView? rtv = null;
        ID3D11Texture2D? texture = null;
        ID3D11ShaderResourceView? srv = null;
        ID3D11VertexShader? vs = null;
        ID3D11PixelShader? ps = null;
        ID3D11SamplerState? sampler = null;
        ID3D11PixelShader? psAlpha = null;
        ID3D11BlendState? blend = null;
        var cursorTextures = new Dictionary<ushort, (ID3D11Texture2D Tex, ID3D11ShaderResourceView Srv, CursorShape Shape)>();
        var tearing = false;
        int texW = 0, texH = 0, winW = 0, winH = 0;
        var announced = false;
        try
        {
            if (D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                    [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0], out ID3D11Device dev, out ID3D11DeviceContext ctx).Failure)
            {
                D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport,
                    [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0], out dev, out ctx).CheckError();
            }
            device = dev;
            context = ctx;
            using (var dxgiDevice = device.QueryInterface<IDXGIDevice>())
            using (var adapter = dxgiDevice.GetAdapter())
            using (var factory = adapter.GetParent<IDXGIFactory2>())
            {
                using (var f5 = factory.QueryInterfaceOrNull<IDXGIFactory5>())
                {
                    if (f5 is not null)
                    {
                        tearing = f5.PresentAllowTearing;
                    }
                }
                GetClientRect(_hwnd, out var rect);
                winW = Math.Max(1, rect.Right);
                winH = Math.Max(1, rect.Bottom);
                swap = factory.CreateSwapChainForHwnd(device, _hwnd, new SwapChainDescription1
                {
                    Width = (uint)winW,
                    Height = (uint)winH,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    BufferUsage = Usage.RenderTargetOutput,
                    BufferCount = 2,
                    SwapEffect = SwapEffect.FlipDiscard,
                    Scaling = Scaling.Stretch,
                    AlphaMode = AlphaMode.Ignore,
                    Flags = tearing ? SwapChainFlags.AllowTearing : SwapChainFlags.None,
                });
                factory.MakeWindowAssociation(_hwnd, WindowAssociationFlags.IgnoreAll);
            }
            using (var back = swap.GetBuffer<ID3D11Texture2D>(0)) rtv = device.CreateRenderTargetView(back);

            var vsCode = Compiler.Compile(Shader, "vs", "video", "vs_4_0");
            var psCode = Compiler.Compile(Shader, "ps", "video", "ps_4_0");
            vs = device.CreateVertexShader(vsCode.Span);
            ps = device.CreatePixelShader(psCode.Span);
            psAlpha = device.CreatePixelShader(Compiler.Compile(Shader, "psa", "video", "ps_4_0").Span);
            var blendDesc = new BlendDescription();
            blendDesc.RenderTarget[0] = new RenderTargetBlendDescription
            {
                BlendEnable = true,
                SourceBlend = Blend.SourceAlpha,
                DestinationBlend = Blend.InverseSourceAlpha,
                BlendOperation = BlendOperation.Add,
                SourceBlendAlpha = Blend.One,
                DestinationBlendAlpha = Blend.Zero,
                BlendOperationAlpha = BlendOperation.Add,
                RenderTargetWriteMask = ColorWriteEnable.All,
            };
            blend = device.CreateBlendState(blendDesc);
            sampler = device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                MaxLOD = float.MaxValue,
            });

            while (!_stop)
            {
                _wake.WaitOne(250);
                if (_stop) break;

                if (_resized)
                {
                    _resized = false;
                    GetClientRect(_hwnd, out var rect);
                    var w = Math.Max(1, rect.Right);
                    var h = Math.Max(1, rect.Bottom);
                    if (w != winW || h != winH)
                    {
                        winW = w;
                        winH = h;
                        context.ClearState();
                        rtv?.Dispose();
                        swap.ResizeBuffers(2, (uint)w, (uint)h, Format.B8G8R8A8_UNorm, tearing ? SwapChainFlags.AllowTearing : SwapChainFlags.None).CheckError();
                        using var back = swap.GetBuffer<ID3D11Texture2D>(0);
                        rtv = device.CreateRenderTargetView(back);
                    }
                }

                var uploaded = false;
                lock (_frameGate)
                {
                    if (_frameFresh)
                    {
                        _frameFresh = false;
                        uploaded = true;
                        if (texture is null || texW != _frameWidth || texH != _frameHeight)
                        {
                            srv?.Dispose();
                            texture?.Dispose();
                            texW = _frameWidth;
                            texH = _frameHeight;
                            texture = device.CreateTexture2D(new Texture2DDescription
                            {
                                Width = (uint)texW,
                                Height = (uint)texH,
                                MipLevels = 1,
                                ArraySize = 1,
                                Format = Format.B8G8R8A8_UNorm,
                                SampleDescription = new SampleDescription(1, 0),
                                Usage = ResourceUsage.Default,
                                BindFlags = BindFlags.ShaderResource,
                            });
                            srv = device.CreateShaderResourceView(texture);
                        }
                        fixed (byte* data = _frame)
                            context.UpdateSubresource(texture, 0, null, (IntPtr)data, (uint)(texW * 4), 0);
                    }
                }

                // Картинка по центру с сохранением пропорций, поля чёрные.
                context.ClearRenderTargetView(rtv!, new Color4(0.04f, 0.04f, 0.05f, 1f));
                if (texture is not null && srv is not null)
                {
                    var scale = Math.Min(winW / (double)texW, winH / (double)texH);
                    var w = (int)Math.Round(texW * scale);
                    var h = (int)Math.Round(texH * scale);
                    _viewX = (winW - w) / 2;
                    _viewY = (winH - h) / 2;
                    _viewW = w;
                    _viewH = h;
                    context.OMSetRenderTargets(rtv!);
                    context.RSSetViewport(new Viewport(_viewX, _viewY, w, h));
                    context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                    context.VSSetShader(vs);
                    context.PSSetShader(ps);
                    context.PSSetShaderResource(0, srv);
                    context.PSSetSampler(0, sampler);
                    context.Draw(3, 0);

                    // Курсор человека поверх картинки — когда помощник им не водит.
                    while (_newShapes.TryDequeue(out var fresh))
                    {
                        if (cursorTextures.Remove(fresh.Id, out var stale)) { stale.Srv.Dispose(); stale.Tex.Dispose(); }
                        fixed (byte* p = fresh.Bgra)
                        {
                            var tex = device.CreateTexture2D(new Texture2DDescription
                            {
                                Width = (uint)fresh.Width, Height = (uint)fresh.Height, MipLevels = 1, ArraySize = 1,
                                Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                                Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource,
                            }, new SubresourceData((IntPtr)p, (uint)(fresh.Width * 4)));
                            cursorTextures[fresh.Id] = (tex, device.CreateShaderResourceView(tex), fresh);
                        }
                    }
                    if (_cursorVisible && !(_mouseInside && ControlEnabled) && cursorTextures.TryGetValue(_cursorId, out var cur))
                    {
                        var cs = Math.Max(0.5, scale);
                        var cx = _viewX + _cursorX * (w - 1) - cur.Shape.HotX * cs;
                        var cy = _viewY + _cursorY * (h - 1) - cur.Shape.HotY * cs;
                        context.RSSetViewport(new Viewport((float)cx, (float)cy, (float)(cur.Shape.Width * cs), (float)(cur.Shape.Height * cs)));
                        context.PSSetShader(psAlpha);
                        context.PSSetShaderResource(0, cur.Srv);
                        context.OMSetBlendState(blend);
                        context.Draw(3, 0);
                        context.OMSetBlendState(null);
                    }
                }
                swap.Present(0, tearing ? PresentFlags.AllowTearing : PresentFlags.None);
                if (texture is not null && uploaded)
                {
                    // Считаем только новые кадры экрана, не перерисовки ради курсора.
                    Interlocked.Increment(ref Rendered);
                    if (!announced)
                    {
                        announced = true;
                        FirstFrame?.Invoke();
                    }
                }
            }
        }
        catch (Exception error)
        {
            Log.Error("Рисование картинки остановилось", error);
        }
        finally
        {
            srv?.Dispose();
            texture?.Dispose();
            sampler?.Dispose();
            foreach (var c in cursorTextures.Values) { c.Srv.Dispose(); c.Tex.Dispose(); }
            blend?.Dispose();
            psAlpha?.Dispose();
            ps?.Dispose();
            vs?.Dispose();
            rtv?.Dispose();
            swap?.Dispose();
            context?.Dispose();
            device?.Dispose();
        }
    }

    // ── Windows ────────────────────────────────────────────────

    private const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000;
    private const uint WM_SETCURSOR = 0x20, WM_MOUSELEAVE = 0x2A3, WM_APP_CURSOR = 0x8001;
    private const uint WM_SIZE = 0x5, WM_SETFOCUS = 0x7, WM_KILLFOCUS = 0x8, WM_ERASEBKGND = 0x14, WM_MOUSEACTIVATE = 0x21,
        WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_LBUTTONDBLCLK = 0x203,
        WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205, WM_RBUTTONDBLCLK = 0x206,
        WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208, WM_MBUTTONDBLCLK = 0x209, WM_MOUSEWHEEL = 0x20A,
        WM_XBUTTONDOWN = 0x20B, WM_XBUTTONUP = 0x20C, WM_XBUTTONDBLCLK = 0x20D, WM_MOUSEHWHEEL = 0x20E,
        WM_CAPTURECHANGED = 0x215;

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint Size, Style;
        public IntPtr WndProc;
        public int ClsExtra, WndExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr IconSm;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr instance, int name);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", EntryPoint = "SetCursor")] private static extern IntPtr SetCursorNative(IntPtr cursor);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT track);
    [DllImport("user32.dll")] private static extern IntPtr CreateIconIndirect(ref ICONINFO info);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPV5HEADER header, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateBitmap(int w, int h, uint planes, uint bits, IntPtr data);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);

    [StructLayout(LayoutKind.Sequential)] private struct TRACKMOUSEEVENT { public uint Size, Flags; public IntPtr Hwnd; public uint HoverTime; }
    [StructLayout(LayoutKind.Sequential)] private struct ICONINFO { public bool IsIcon; public int HotspotX, HotspotY; public IntPtr Mask, Color; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPV5HEADER
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPels, YPels; public uint ClrUsed, ClrImportant;
        public uint RedMask, GreenMask, BlueMask, AlphaMask, CSType;
        public int EndpointsRX, EndpointsRY, EndpointsRZ, EndpointsGX, EndpointsGY, EndpointsGZ, EndpointsBX, EndpointsBY, EndpointsBZ;
        public uint GammaRed, GammaGreen, GammaBlue, Intent, ProfileData, ProfileSize, Reserved;
    }
}
