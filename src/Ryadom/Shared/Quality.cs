using Ryadom.Live;

namespace Ryadom.Shared;

public static class Quality
{
    /// <summary>Наибольшая высота кадра для качества; 0 — как на экране.</summary>
    public static int MaxHeight(string? key) => key switch
    {
        "low" => 720,
        "max" => 0,
        _ => 1080,
    };

    /// <summary>Битрейт: 1080p30 экрана — 5 Мбит, дальше по площади и (мягче) по кадрам.</summary>
    public static ulong Bitrate(string? key, int width, int height, int fps)
    {
        var area = width * (double)height / (1920.0 * 1080);
        var rate = 5_000_000 * area * Math.Pow(fps / 30.0, 0.7);
        if (key == "low") rate *= 0.7;
        if (key == "max") rate *= 1.4;
        return (ulong)Math.Clamp(rate, 1_200_000, 30_000_000);
    }

    public static int ClampFps(int? fps) => Math.Clamp(fps ?? 60, 30, 90);
}

public static class Connector
{
    /// <summary>Войти в комнату по первому отозвавшемуся адресу LiveKit.</summary>
    public static async Task<LiveRoom> ConnectAsync(string[] urls, string token, bool subscribe, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var url in urls)
        {
            try { return await LiveRoom.ConnectAsync(url, token, subscribe, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                last = error;
                Log.Warn($"LiveKit {url}: {error.Message}");
            }
        }
        Log.Error("Видеосервер недоступен", last);
        throw new Net.ApiException("Не удалось подключиться к видеосерверу. Проверьте интернет.");
    }
}
