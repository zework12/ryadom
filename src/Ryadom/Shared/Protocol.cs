using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ryadom.Shared;

/// <summary>
/// Что ходит между помощником и человеком по каналу данных LiveKit.
/// Ввод — короткими двоичными пакетами (тема "i"): движение мыши без гарантии
/// доставки (следующее всё равно придёт через миг), нажатия — с гарантией.
/// Остальное — JSON (тема "c"), всегда с гарантией.
/// </summary>
public static class Protocol
{
    public const string InputTopic = "i";
    public const string ControlTopic = "c";

    // ── Ввод ───────────────────────────────────────────────────

    public const byte Move = 1;      // [1][u16 x][u16 y] — доли экрана 0..65535
    public const byte Button = 2;    // [2][кнопка][1 нажата / 0 отпущена]
    public const byte Wheel = 3;     // [3][1 — горизонтально][i16 сколько]
    public const byte Key = 4;       // [4][u16 скан-код][флаги: 1 — отпущена, 2 — расширенная]
    public const byte ReleaseAll = 5; // [5] — отпустить всё нажатое (помощник ушёл из окна)
    public const byte Cursor = 6;    // человек → помощник: [6][u16 x][u16 y][1 виден][u16 номер формы]

    public const byte Left = 0, Right = 1, Middle = 2, X1 = 3, X2 = 4;
    public const byte KeyUp = 1, KeyExtended = 2;

    public static byte[] EncodeMove(double fx, double fy)
    {
        var data = new byte[5];
        data[0] = Move;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(1), ToU16(fx));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(3), ToU16(fy));
        return data;
    }

    public static byte[] EncodeButton(byte button, bool down) => [Button, button, (byte)(down ? 1 : 0)];

    public static byte[] EncodeWheel(int delta, bool horizontal)
    {
        var data = new byte[4];
        data[0] = Wheel;
        data[1] = (byte)(horizontal ? 1 : 0);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(2), (short)Math.Clamp(delta, short.MinValue, short.MaxValue));
        return data;
    }

    public static byte[] EncodeKey(ushort scan, bool up, bool extended)
    {
        var data = new byte[4];
        data[0] = Key;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(1), scan);
        data[3] = (byte)((up ? KeyUp : 0) | (extended ? KeyExtended : 0));
        return data;
    }

    public static byte[] EncodeReleaseAll() => [ReleaseAll];

    public static byte[] EncodeCursor(double fx, double fy, bool visible, ushort shape)
    {
        var data = new byte[8];
        data[0] = Cursor;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(1), ToU16(fx));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(3), ToU16(fy));
        data[5] = (byte)(visible ? 1 : 0);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), shape);
        return data;
    }

    public static bool TryDecodeCursor(ReadOnlySpan<byte> data, out double fx, out double fy, out bool visible, out ushort shape)
    {
        fx = fy = 0; visible = false; shape = 0;
        if (data.Length < 8 || data[0] != Cursor) return false;
        fx = BinaryPrimitives.ReadUInt16LittleEndian(data[1..]) / 65535.0;
        fy = BinaryPrimitives.ReadUInt16LittleEndian(data[3..]) / 65535.0;
        visible = data[5] != 0;
        shape = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
        return true;
    }

    /// <summary>Картинку курсора — сжатой (в основном прозрачная, жмётся в разы):
    /// сообщения канала данных должны оставаться маленькими.</summary>
    public static string PackPixels(byte[] pixels)
    {
        using var output = new MemoryStream();
        using (var deflate = new System.IO.Compression.DeflateStream(output, System.IO.Compression.CompressionLevel.Optimal))
            deflate.Write(pixels);
        return Convert.ToBase64String(output.ToArray());
    }

    public static byte[] UnpackPixels(string text)
    {
        using var input = new MemoryStream(Convert.FromBase64String(text));
        using var deflate = new System.IO.Compression.DeflateStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    private static ushort ToU16(double f) => (ushort)Math.Round(Math.Clamp(f, 0, 1) * 65535);

    // ── Управление ─────────────────────────────────────────────

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static byte[] EncodeControl(Control message) => JsonSerializer.SerializeToUtf8Bytes(message, Json);

    public static Control? DecodeControl(ReadOnlySpan<byte> data)
    {
        try { return JsonSerializer.Deserialize<Control>(data, Json); }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// Сообщение управления. T — что это:
/// hello (помощник → человек: Name), accept / deny (человек → помощник),
/// bye (любой: ухожу), settings (помощник → человек: Fps, Quality, Monitor),
/// monitors (человек → помощник: Monitors, Monitor — какой показан),
/// ping / pong (Id, замер задержки), clip (Text — буфер обмена), keys (Combo — сочетание),
/// cursor (человек → помощник: форма курсора Id, W×H, точка Hx/Hy, Text — BGRA в base64),
/// locks (помощник → человек: Caps, Num — как горят у помощника).
/// </summary>
public sealed class Control
{
    public string T { get; set; } = "";
    public string? Name { get; set; }
    public int? Fps { get; set; }
    public string? Quality { get; set; }
    public int? Monitor { get; set; }
    public List<MonitorInfo>? Monitors { get; set; }
    public long? Id { get; set; }
    public string? Text { get; set; }
    public string? Combo { get; set; }
    public int? W { get; set; }
    public int? H { get; set; }
    public int? Hx { get; set; }
    public int? Hy { get; set; }
    public bool? Caps { get; set; }
    /// <summary>Кусок большого ответа (agent-desktop): номер и сколько всего.</summary>
    public int? Part { get; set; }
    public int? Parts { get; set; }
    public bool? Num { get; set; }
}

public sealed class MonitorInfo
{
    public int Index { get; set; }
    public string Title { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
}
