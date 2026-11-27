using MixMoghimi.Models;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace MixMoghimi.Services;

public sealed class MixApiClient : IDisposable
{
    private static readonly Uri Endpoint = new("https://hxfnoaeyjnkecegpppcy.supabase.co/functions/v1/mix-api");
    private const string PublishableKey = "sb_publishable_nqjUZPJ_cr_SfzDntkbsLg_v6oaJNqY";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(8)
    }) { Timeout = TimeSpan.FromSeconds(14) };

    private readonly DeviceIdentityService _identity;
    private readonly string _installationHash;
    private string? _deviceToken;

    public MixApiClient(DeviceIdentityService identity)
    {
        _identity = identity;
        _installationHash = identity.GetInstallationHash();
        _deviceToken = identity.LoadDeviceToken();
    }

    public string InstallationHash => _installationHash;

    public async Task EnsureRegisteredAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(_deviceToken))
        {
            try
            {
                TrialSnapshot existing = await GetSnapshotAsync(cancellationToken);
                if (existing.Ok) return;
            }
            catch (MixApiException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _deviceToken = null;
            }
        }

        using JsonDocument response = await SendAsync(new
        {
            action = "register",
            installation_hash = _installationHash
        }, includeToken: false, cancellationToken);

        string token = RequiredString(response.RootElement, "device_token");
        _identity.SaveDeviceToken(token);
        _deviceToken = token;
    }

    public async Task<TrialSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        using JsonDocument response = await SendAsync(new { action = "snapshot" }, true, cancellationToken);
        return response.RootElement.Deserialize<TrialSnapshot>(JsonOptions)
            ?? throw new InvalidDataException("پاسخ سهمیه از سرور معتبر نیست.");
    }

    public async Task<IReadOnlyList<ServerEntry>> GetServersAsync(CancellationToken cancellationToken = default)
    {
        using JsonDocument response = await SendAsync(new { action = "servers" }, true, cancellationToken);
        if (!response.RootElement.TryGetProperty("entries", out JsonElement entries) || entries.ValueKind != JsonValueKind.Array)
            return Array.Empty<ServerEntry>();

        var list = new List<ServerEntry>();
        foreach (JsonElement item in entries.EnumerateArray())
        {
            string id = item.TryGetProperty("id", out var idNode) ? idNode.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");
            string title = item.TryGetProperty("title", out var titleNode) ? titleNode.GetString() ?? "سرور Moghimi" : "سرور Moghimi";
            int priority = item.TryGetProperty("priority", out var priorityNode) && priorityNode.TryGetInt32(out int p) ? p : 0;
            list.Add(new ServerEntry(id, title, priority));
        }
        return list;
    }

    public async Task<string> GetServerConfigAsync(string serverId, CancellationToken cancellationToken = default)
    {
        using JsonDocument response = await SendAsync(new { action = "server_config", server_id = serverId }, true, cancellationToken);
        string config = ReadString(response.RootElement, "config_text") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(config))
            throw new InvalidDataException("کانفیگ این سرور خالی یا غیرفعال است.");
        return config;
    }

    public async Task<TrialSnapshot> StartSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        using JsonDocument response = await SendAsync(new
        {
            action = "start",
            session_id = sessionId.ToString()
        }, true, cancellationToken);
        EnsureResponseOk(response.RootElement);
        return response.RootElement.Deserialize<TrialSnapshot>(JsonOptions)
            ?? throw new InvalidDataException("پاسخ شروع نشست از سرور معتبر نیست.");
    }

    public async Task<TrialSnapshot> SendHeartbeatAsync(
        Guid sessionId, long sequence, long rxTotal, long txTotal, bool isFinal,
        CancellationToken cancellationToken = default)
    {
        using JsonDocument response = await SendAsync(new
        {
            action = "heartbeat",
            session_id = sessionId.ToString(),
            sequence,
            rx_total = rxTotal,
            tx_total = txTotal,
            is_final = isFinal
        }, true, cancellationToken);
        EnsureResponseOk(response.RootElement);
        return response.RootElement.Deserialize<TrialSnapshot>(JsonOptions)
            ?? throw new InvalidDataException("پاسخ ثبت مصرف از سرور معتبر نیست.");
    }

    private async Task<JsonDocument> SendAsync(object payload, bool includeToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };
        request.Headers.TryAddWithoutValidation("apikey", PublishableKey);
        request.Headers.TryAddWithoutValidation("x-mix-installation-hash", _installationHash);
        if (includeToken)
        {
            if (string.IsNullOrWhiteSpace(_deviceToken))
                throw new MixApiException("device_token_missing", "شناسهٔ نصب برنامه ثبت نشده است.", HttpStatusCode.Unauthorized);
            request.Headers.TryAddWithoutValidation("x-mix-device-token", _deviceToken);
        }

        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        string content = await response.Content.ReadAsStringAsync(cancellationToken);
        JsonDocument document;
        try { document = JsonDocument.Parse(content); }
        catch (JsonException)
        {
            throw new MixApiException("invalid_server_response", "پاسخ سرور قابل خواندن نیست.", response.StatusCode);
        }

        if (!response.IsSuccessStatusCode)
        {
            string code = ReadString(document.RootElement, "code") ?? "service_unavailable";
            string message = ReadString(document.RootElement, "message") ?? FriendlyError(code);
            document.Dispose();
            throw new MixApiException(code, message, response.StatusCode);
        }
        return document;
    }

    private static void EnsureResponseOk(JsonElement root)
    {
        if (root.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.False)
        {
            string code = ReadString(root, "code") ?? "request_failed";
            throw new MixApiException(code, FriendlyError(code), HttpStatusCode.Conflict);
        }
    }

    private static string RequiredString(JsonElement root, string name) =>
        ReadString(root, name) is { Length: > 0 } value ? value : throw new InvalidDataException("پاسخ ثبت دستگاه ناقص است.");

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement node) && node.ValueKind == JsonValueKind.String ? node.GetString() : null;

    private static string FriendlyError(string code) => code switch
    {
        "trial_exhausted" => "سهمیهٔ سه‌ساعتهٔ این هفته تمام شده است.",
        "session_already_active" => "یک نشست فعال دیگر برای این دستگاه وجود دارد.",
        "lease_expired" => "ارتباط با سرور سهمیه قطع شد؛ اتصال برای امنیت بسته می‌شود.",
        "server_catalog_empty" => "هنوز کانفیگی در پنل ویندوز ثبت نشده است.",
        "device_blocked" => "دسترسی این دستگاه غیرفعال شده است.",
        _ => "ارتباط با سرویس ویندوز برقرار نشد. اینترنت را بررسی کن و دوباره تلاش کن."
    };

    public void Dispose() => _http.Dispose();
}

public sealed class MixApiException(string code, string message, HttpStatusCode statusCode) : Exception(message)
{
    public string Code { get; } = code;
    public HttpStatusCode StatusCode { get; } = statusCode;
}
