using System.Text.Json.Serialization;

namespace MixMoghimi.Models;

public sealed record ServerEntry(string Id, string Title, int Priority = 0);

public sealed record ShadowsocksServer(
    string Server,
    int ServerPort,
    string Method,
    string Password,
    string DisplayName);

public sealed class TrialSnapshot
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }
    [JsonPropertyName("access_allowed")]
    public bool AccessAllowed { get; set; }
    [JsonPropertyName("session_active")]
    public bool SessionActive { get; set; }
    [JsonPropertyName("trial_limit_ms")]
    public long TrialLimitMs { get; set; }
    [JsonPropertyName("trial_used_ms")]
    public long TrialUsedMs { get; set; }
    [JsonPropertyName("trial_remaining_ms")]
    public long TrialRemainingMs { get; set; }
    [JsonPropertyName("period_started_at")]
    public DateTimeOffset? PeriodStartedAt { get; set; }
    [JsonPropertyName("reset_at")]
    public DateTimeOffset? ResetAt { get; set; }
    [JsonPropertyName("server_time")]
    public DateTimeOffset? ServerTime { get; set; }
    [JsonPropertyName("week_download_bytes")]
    public long WeekDownloadBytes { get; set; }
    [JsonPropertyName("week_upload_bytes")]
    public long WeekUploadBytes { get; set; }
    [JsonPropertyName("total_download_bytes")]
    public long TotalDownloadBytes { get; set; }
    [JsonPropertyName("total_upload_bytes")]
    public long TotalUploadBytes { get; set; }
}

public sealed class ApiEnvelope<T>
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }
    public string? Code { get; set; }
    public T? Data { get; set; }
}
