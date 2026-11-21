using System.Runtime.InteropServices;
using System.Windows;
using Ryadom.Shared;

namespace Ryadom.Host;

/// <summary>
/// Курсор человека — отдельно от видео. Картинка экрана снимается без
/// курсора, а его место и форма идут по каналу данных: у помощника курсор
/// той же формы (стрелка, рука, I-образный…) и стоит там же, без задержки
/// кодека. Каждая форма уходит один раз, дальше — только её номер.
/// </summary>
public sealed unsafe class CursorWatcher : IDisposable
{
    private readonly Action<byte[]> _position;
    private readonly Action<Control> _shape;
    private readonly Thread _thread;
    private readonly Dictionary<IntPtr, ushort> _ids = [];
    private volatile bool _stop;
    private Int32Rect _monitor;
    private ushort _nextId = 1;

    /// <param name="position">Пакет положения (без гарантии доставки).</param>
    /// <param name="shape">Новая форма курсора (с гарантией).</param>
    public CursorWatcher(Int32Rect monitor, Action<byte[]> position, Action<Control> shape)
    {
        _monitor = monitor;
        _position = position;
        _shape = shape;
        _thread = new Thread(Run) { IsBackground = true, Name = "Курсор" };
        _thread.Start();
    }

    public void SetMonitor(Int32Rect monitor) => _monitor = monitor;

    /// <summary>Помощник подключился заново — формы отправить ещё раз.</summary>
    public void ResendShapes()
    {
        lock (_ids) _ids.Clear();
    }

    private void Run()
    {
        var last = (X: -1, Y: -1, Id: (ushort)0, Visible: false);
        var lastSent = Environment.TickCount64;
        while (!_stop)
        {
            Thread.Sleep(8);
            var info = new CURSORINFO { Size = Marshal.SizeOf<CURSORINFO>() };
            if (!GetCursorInfo(ref info)) continue;
            var m = _monitor;
            var visible = (info.Flags & 1) != 0 && info.Cursor != IntPtr.Zero
                          && info.X >= m.X && info.Y >= m.Y && info.X < m.X + m.Width && info.Y < m.Y + m.Height;
            ushort id = 0;
            if (visible)
            {
                try { id = ShapeId(info.Cursor); }
                catch (Exception error) { Log.Warn($"Форма курсора: {error.Message}"); }
            }
            var now = Environment.TickCount64;
            if (info.X == last.X && info.Y == last.Y && id == last.Id && visible == last.Visible && now - lastSent < 1000) continue;
            last = (info.X, info.Y, id, visible);
            lastSent = now;
            var fx = (info.X - m.X) / (double)Math.Max(1, m.Width - 1);
            var fy = (info.Y - m.Y) / (double)Math.Max(1, m.Height - 1);
            _position(Protocol.EncodeCursor(fx, fy, visible, id));
        }
    }

    private ushort ShapeId(IntPtr cursor)
    {
        lock (_ids)
        {
            if (_ids.TryGetValue(cursor, out var known)) return known;
        }
        var shape = Capture(cursor);
        if (shape is null) return 0;
        ushort id;
        lock (_ids)
        {
            id = _nextId++;
            if (_nextId == 0) _nextId = 1;
            _ids[cursor] = id;
        }
        shape.Id = id;
        _shape(shape);
        return id;
    }

    /// <summary>Снять форму курсора в BGRA с прозрачностью. Чёрно-белые курсоры
    /// (I-образный и пр.) рисуются на чёрном и на белом: так видно, где они
    /// прозрачные, а где инвертируют фон — такие точки делаем чёрными с белой каймой.</summary>
    private static Control? Capture(IntPtr cursor)
    {
        if (!GetIconInfo(cursor, out var icon)) return null;
        try
        {
            int width, height;
            if (icon.Color != IntPtr.Zero)
            {
                GetObject(icon.Color, Marshal.SizeOf<BITMAP>(), out var bm);
                width = bm.Width; height = bm.Height;
            }
            else
            {
                GetObject(icon.Mask, Marshal.SizeOf<BITMAP>(), out var bm);
                width = bm.Width; height = bm.Height / 2;
            }
            width = Math.Clamp(width, 1, 128);
            height = Math.Clamp(height, 1, 128);
            var onBlack = Render(cursor, width, height, 0x000000);
            var onWhite = Render(cursor, width, height, 0xFFFFFF);
            var pixels = new byte[width * height * 4];
            var invert = new bool[width * height];
            for (var i = 0; i < width * height; i++)
            {
                int b0 = onBlack[i * 4], g0 = onBlack[i * 4 + 1], r0 = onBlack[i * 4 + 2];
                int b1 = onWhite[i * 4], g1 = onWhite[i * 4 + 1], r1 = onWhite[i * 4 + 2];
                // alpha = 1 − (на белом − на чёрном); отрицательное — инверсия фона.
                var diff = ((b1 - b0) + (g1 - g0) + (r1 - r0)) / 3;
                if (diff < -16)
                {
                    invert[i] = true;
                    pixels[i * 4 + 3] = 255;
                    continue;
                }
                var alpha = Math.Clamp(255 - diff, 0, 255);
                if (alpha == 0) continue;
                pixels[i * 4] = (byte)Math.Min(255, b0 * 255 / alpha);
                pixels[i * 4 + 1] = (byte)Math.Min(255, g0 * 255 / alpha);
                pixels[i * 4 + 2] = (byte)Math.Min(255, r0 * 255 / alpha);
                pixels[i * 4 + 3] = (byte)alpha;
            }
            // Белая кайма вокруг инвертирующих точек — видно и на тёмном.
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var i = y * width + x;
                if (invert[i] || pixels[i * 4 + 3] != 0) continue;
                var near = false;
                for (var dy = -1; dy <= 1 && !near; dy++)
                for (var dx = -1; dx <= 1 && !near; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    near = nx >= 0 && ny >= 0 && nx < width && ny < height && invert[ny * width + nx];
                }
                if (near) { pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = 255; pixels[i * 4 + 3] = 255; }
            }
            return new Control
            {
                T = "cursor",
                W = width,
                H = height,
                Hx = icon.HotspotX,
                Hy = icon.HotspotY,
                Text = Protocol.PackPixels(pixels),
            };
        }
        finally
        {
            if (icon.Color != IntPtr.Zero) DeleteObject(icon.Color);
            if (icon.Mask != IntPtr.Zero) DeleteObject(icon.Mask);
        }
    }

    private static byte[] Render(IntPtr cursor, int width, int height, uint background)
    {
        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        ReleaseDC(IntPtr.Zero, screen);
        var header = new BITMAPINFOHEADER { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
        var bitmap = CreateDIBSection(dc, ref header, 0, out var bits, IntPtr.Zero, 0);
        var old = SelectObject(dc, bitmap);
        try
        {
            var span = new Span<uint>((void*)bits, width * height);
            span.Fill(background);
            DrawIconEx(dc, 0, 0, cursor, width, height, 0, IntPtr.Zero, 3 /* DI_NORMAL */);
            GdiFlush();
            return new Span<byte>((void*)bits, width * height * 4).ToArray();
        }
        finally
        {
            SelectObject(dc, old);
            DeleteObject(bitmap);
            DeleteDC(dc);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(500);
    }

    [StructLayout(LayoutKind.Sequential)] private struct CURSORINFO { public int Size; public int Flags; public IntPtr Cursor; public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct ICONINFO { public bool IsIcon; public int HotspotX, HotspotY; public IntPtr Mask, Color; }
    [StructLayout(LayoutKind.Sequential)] private struct BITMAP { public int Type, Width, Height, WidthBytes; public ushort Planes, BitsPixel; public IntPtr Bits; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int Size, Width, Height;
        public ushort Planes, BitCount;
        public int Compression, SizeImage, XPels, YPels, ClrUsed, ClrImportant;
    }

    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO info);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int w, int h, int step, IntPtr brush, int flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr obj, int size, out BITMAP bitmap);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER header, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
}
