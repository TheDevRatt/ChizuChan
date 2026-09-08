using System.Text.Json.Serialization;

namespace ChizuChan.Services;

[JsonConverter(typeof(JsonStringEnumConverter<YouTubeLongMediaState>))]
public enum YouTubeLongMediaState { Queued, Running, Succeeded, Failed, Interrupted }

public sealed record YouTubeLongMediaJob
{
    public required string Id { get; init; }
    public required ulong OwnerUserId { get; init; }
    public required string VideoId { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public YouTubeLongMediaState State { get; init; }
    public string Message { get; init; } = "Waiting for a download worker.";
    public ulong? DeliveredMessageId { get; init; }
    public int DeliveryAttempts { get; init; }
    public string? DeliveryError { get; init; }
    public DateTimeOffset NextDeliveryAt { get; init; }
    [JsonIgnore] public bool IsTerminal => State is not (YouTubeLongMediaState.Queued or YouTubeLongMediaState.Running);
}
