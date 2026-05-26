using System.Text.Json;
using System.Text.Json.Serialization;

namespace Outbox.Relay.Models;

public sealed record Message(
    string Id,
    string From,
    string To,
    string Subject,
    string Body,
    [property: JsonIgnore] string? MetadataJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt)
{
    public JsonElement? Metadata =>
        string.IsNullOrEmpty(MetadataJson) ? null : JsonDocument.Parse(MetadataJson).RootElement.Clone();
}

public sealed record SendMessageRequest(string To, string Subject, string Body, JsonElement? Metadata);

public sealed record SendMessageResponse(string Id);

public sealed record InboxItem(
    string Id,
    string From,
    string To,
    string Subject,
    DateTimeOffset CreatedAt,
    bool Read);

public sealed record CreateUserRequest(string Handle);

public sealed record CreateUserResponse(string Handle, string Token);

public sealed record AckResponse(bool Ok);

public sealed record CreateInviteRequest(string Handle, int? TtlHours);

public sealed record CreateInviteResponse(string Code, string Handle, string Url, DateTimeOffset ExpiresAt);

public sealed record RedeemInviteResponse(string Handle, string Token, string RelayUrl);
