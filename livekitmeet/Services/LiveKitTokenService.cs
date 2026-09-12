namespace livekitmeet.Services;

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Livekit.Server.Sdk.Dotnet;

public record ConnectionDetailsDto(
    string ServerUrl,
    string RoomName,
    string ParticipantToken,
    string ParticipantName,
    string ParticipantIdentity
);

public record ParticipantDto(
    string Identity,
    string Name,
    bool IsLocal,
    bool IsAudioEnabled,
    bool IsVideoEnabled,
    bool IsSpeaking
);

public interface ILiveKitTokenService
{
    ConnectionDetailsDto CreateConnectionDetails(string roomName, ClaimsPrincipal authenticatedUser, string? metadata = null);
}

public class LiveKitTokenService : ILiveKitTokenService
{
    private readonly IConfiguration _configuration;

    public LiveKitTokenService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public ConnectionDetailsDto CreateConnectionDetails(string roomName, ClaimsPrincipal authenticatedUser, string? metadata = null)
    {
        var participantName = authenticatedUser.FindFirst("display_name")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(participantName))
        {
            throw new InvalidOperationException("The authenticated user does not have a display name.");
        }

        var apiKey = _configuration["LiveKit:ApiKey"] ?? Environment.GetEnvironmentVariable("LIVEKIT_API_KEY");
        var apiSecret = _configuration["LiveKit:ApiSecret"] ?? Environment.GetEnvironmentVariable("LIVEKIT_API_SECRET");
        var serverUrl = _configuration["LiveKit:Url"] ?? Environment.GetEnvironmentVariable("LIVEKIT_URL");

        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret) || string.IsNullOrEmpty(serverUrl))
        {
            throw new InvalidOperationException("LiveKit credentials (ApiKey, ApiSecret, Url) are not configured.");
        }

        var randomPostfix = Guid.NewGuid().ToString("N")[..4];
        var identity = $"{participantName}__{randomPostfix}";

        string jwt;

        // Livekit.Server.Sdk.Dotnet enforces a 256-bit (>= 32 bytes) secret.
        // For local development environments where apiSecret may be shorter (e.g. 'secret'),
        // generate a valid RFC 7519 HMAC-SHA256 JWT containing the exact LiveKit video grants.
        if (Encoding.UTF8.GetByteCount(apiSecret) >= 32)
        {
            var token = new AccessToken(apiKey, apiSecret)
                .WithIdentity(identity)
                .WithName(participantName)
                .WithGrants(new VideoGrants
                {
                    Room = roomName,
                    RoomJoin = true,
                    CanPublish = true,
                    CanPublishData = true,
                    CanSubscribe = true
                })
                .WithTtl(TimeSpan.FromHours(2));

            if (!string.IsNullOrEmpty(metadata))
            {
                token.WithMetadata(metadata);
            }

            jwt = token.ToJwt();
        }
        else
        {
            jwt = GenerateLiveKitJwt(apiKey, apiSecret, identity, participantName, roomName, metadata);
        }

        return new ConnectionDetailsDto(serverUrl, roomName, jwt, participantName, identity);
    }

    private static string GenerateLiveKitJwt(
        string apiKey,
        string apiSecret,
        string identity,
        string participantName,
        string roomName,
        string? metadata)
    {
        var now = DateTimeOffset.UtcNow;
        var exp = now.AddHours(2).ToUnixTimeSeconds();
        var nbf = now.ToUnixTimeSeconds();

        var header = new { alg = "HS256", typ = "JWT" };
        var payload = new Dictionary<string, object>
        {
            { "exp", exp },
            { "nbf", nbf },
            { "iss", apiKey },
            { "sub", identity },
            { "name", participantName },
            { "video", new Dictionary<string, object>
                {
                    { "room", roomName },
                    { "roomJoin", true },
                    { "canPublish", true },
                    { "canPublishData", true },
                    { "canSubscribe", true }
                }
            }
        };

        if (!string.IsNullOrEmpty(metadata))
        {
            payload["metadata"] = metadata;
        }

        var headerJson = JsonSerializer.Serialize(header);
        var payloadJson = JsonSerializer.Serialize(payload);

        var headerBase64 = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        var payloadBase64 = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
        var message = $"{headerBase64}.{payloadBase64}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(apiSecret));
        var signature = Base64UrlEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes(message)));

        return $"{message}.{signature}";
    }

    private static string Base64UrlEncode(byte[] input)
    {
        return Convert.ToBase64String(input)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
