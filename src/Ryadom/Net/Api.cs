using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ryadom.Shared;

namespace Ryadom.Net;

public sealed record HostGrant(string Code, string HostKey, string[] Urls, string Token);

public sealed record JoinGrant(string[] Urls, string Token, string HostName);

/// <summary>Ошибка, которую можно показать человеку как есть.</summary>
public sealed class ApiException(string message) : Exception(message);

/// <summary>
/// Короткие запросы к серверу «Рядом» (коды и пропуски). Сервер отвечает и
/// на 443, и на 2053: некоторые провайдеры режут одно из них — пробуем по
/// очереди, каждый адрес с повтором.
/// </summary>
public static class Api
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string[] Bases { get; set; } =
    [
        "https://94.159.117.75.sslip.io/ryadom",
        "https://94.159.117.75.sslip.io:2053/ryadom",
    ];

    public static Task<HostGrant> HostAsync(string name, CancellationToken ct) =>
        PostAsync<HostGrant>("/api/host", new { name }, ct);

    public static Task<HostGrant> RenewAsync(string code, string hostKey, CancellationToken ct) =>
        PostAsync<HostGrant>("/api/host/renew", new { code, hostKey }, ct);

    public static Task<JoinGrant> JoinAsync(string code, string name, CancellationToken ct) =>
        PostAsync<JoinGrant>("/api/join", new { code, name }, ct);

    private static async Task<T> PostAsync<T>(string path, object body, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            foreach (var root in Bases)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // Длина тела — заранее: сервер не читает тело кусками (chunked).
                    using var content = new StringContent(JsonSerializer.Serialize(body, Json), System.Text.Encoding.UTF8, "application/json");
                    using var response = await Http.PostAsync(root + path, content, ct);
                    if (response.IsSuccessStatusCode)
                        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
                    var error = await ReadError(response, ct);
                    // Ответ сервера («нет такого кода») не лечится другим адресом.
                    throw new ApiException(error);
                }
                catch (ApiException) { throw; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    last = error;
                    Log.Warn($"Сервер {root}: {error.Message}");
                }
            }
            await Task.Delay(500 * (attempt + 1), ct);
        }
        Log.Error("Сервер недоступен", last);
        throw new ApiException("Нет связи с сервером. Проверьте интернет и попробуйте ещё раз.");
    }

    private static async Task<string> ReadError(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var data = await response.Content.ReadFromJsonAsync<ErrorBody>(Json, ct);
            if (!string.IsNullOrWhiteSpace(data?.Error)) return data.Error;
        }
        catch (Exception) { }
        return $"Сервер ответил ошибкой {(int)response.StatusCode}.";
    }

    private sealed class ErrorBody
    {
        [JsonPropertyName("error")] public string? Error { get; set; }
    }
}
