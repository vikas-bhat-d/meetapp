using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using livekitmeet.Data;
using Microsoft.EntityFrameworkCore;

namespace livekitmeet.Services;

public sealed record FcmSendResult(
    bool Configured,
    int RegisteredCount,
    int DeliveredCount,
    string? Error = null);

public interface IFirebasePushNotificationService
{
    Task<FcmSendResult> SendAsync(
        Guid userId,
        string title,
        string body,
        IReadOnlyDictionary<string, string> data,
        CancellationToken cancellationToken = default);
}

public sealed class FirebasePushNotificationService : IFirebasePushNotificationService
{
    private const string OAuthTokenUrl = "https://oauth2.googleapis.com/token";
    private const string MessagingScope = "https://www.googleapis.com/auth/firebase.messaging";
    private static readonly SemaphoreSlim OAuthLock = new(1, 1);
    private static string? _cachedOAuthToken;
    private static DateTime _cachedOAuthTokenExpiresAtUtc;

    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<FirebasePushNotificationService> _logger;

    public FirebasePushNotificationService(
        AppDbContext db,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<FirebasePushNotificationService> logger)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<FcmSendResult> SendAsync(
        Guid userId,
        string title,
        string body,
        IReadOnlyDictionary<string, string> data,
        CancellationToken cancellationToken = default)
    {
        var devices = await _db.PushDevices
            .Where(device => device.UserId == userId &&
                             device.Platform == "android" &&
                             device.IsActive)
            .ToListAsync(cancellationToken);
        if (devices.Count == 0)
        {
            return new FcmSendResult(true, 0, 0);
        }

        try
        {
            var credentials = LoadCredentials();
            if (credentials is null)
            {
                return new FcmSendResult(
                    false,
                    devices.Count,
                    0,
                    "Firebase server credentials are not configured.");
            }

            var accessToken = await GetOAuthTokenAsync(
                credentials.ClientEmail,
                credentials.PrivateKey,
                cancellationToken);
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var endpoint = $"https://fcm.googleapis.com/v1/projects/{Uri.EscapeDataString(credentials.ProjectId)}/messages:send";
            var delivered = 0;

            foreach (var device in devices)
            {
                var payload = new
                {
                    message = new
                    {
                        token = device.PushToken,
                        notification = new { title, body },
                        data,
                        android = new
                        {
                            priority = "HIGH",
                            notification = new
                            {
                                channel_id = "incoming-calls",
                                sound = "default"
                            }
                        }
                    }
                };

                using var response = await client.PostAsJsonAsync(endpoint, payload, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    delivered++;
                    device.LastSeenAtUtc = DateTime.UtcNow;
                }
                else
                {
                    var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (responseBody.Contains("UNREGISTERED", StringComparison.OrdinalIgnoreCase) ||
                        responseBody.Contains("registration-token-not-registered", StringComparison.OrdinalIgnoreCase))
                    {
                        device.IsActive = false;
                    }

                    _logger.LogWarning(
                        "FCM delivery failed for push device {DeviceId}: {StatusCode} {ResponseBody}",
                        device.Id,
                        response.StatusCode,
                        responseBody);
                }
            }

            await _db.SaveChangesAsync(cancellationToken);
            return delivered == 0
                ? new FcmSendResult(
                    true,
                    devices.Count,
                    0,
                    "Firebase could not deliver the notification to the registered device.")
                : new FcmSendResult(true, devices.Count, delivered);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or CryptographicException or ArgumentException or JsonException)
        {
            _logger.LogError(ex, "FCM delivery failed for user {UserId}.", userId);
            return new FcmSendResult(
                true,
                devices.Count,
                0,
                "Firebase could not deliver the notification. Check the server logs.");
        }
    }

    private FirebaseCredentials? LoadCredentials()
    {
        var configuredProjectId = NullIfWhiteSpace(_configuration["Firebase:ProjectId"]);
        var configuredClientEmail = NullIfWhiteSpace(_configuration["Firebase:ClientEmail"]);
        var configuredPrivateKey = NullIfWhiteSpace(_configuration["Firebase:PrivateKey"]);
        var serviceAccountPath = NullIfWhiteSpace(_configuration["Firebase:ServiceAccountPath"]);

        // Also accept a path in Firebase:PrivateKey so an existing setup can be
        // fixed without copying a private key into configuration.
        if (string.IsNullOrWhiteSpace(serviceAccountPath) &&
            !string.IsNullOrWhiteSpace(configuredPrivateKey) &&
            File.Exists(configuredPrivateKey))
        {
            serviceAccountPath = configuredPrivateKey;
            configuredPrivateKey = null;
        }

        if (!string.IsNullOrWhiteSpace(serviceAccountPath))
        {
            if (!File.Exists(serviceAccountPath))
            {
                throw new InvalidOperationException(
                    $"Firebase service-account file was not found: {serviceAccountPath}");
            }

            var serviceAccountJson = File.ReadAllText(serviceAccountPath);
            var serviceAccount = JsonSerializer.Deserialize<FirebaseServiceAccount>(serviceAccountJson);
            if (serviceAccount is null)
            {
                throw new InvalidOperationException("Firebase service-account JSON is empty.");
            }

            // When a service-account file is configured, its values are the
            // source of truth. This also prevents stale placeholder secrets
            // such as "$firebase.private_key" from overriding the JSON key.
            configuredProjectId = NullIfWhiteSpace(serviceAccount.ProjectId) ?? configuredProjectId;
            configuredClientEmail = NullIfWhiteSpace(serviceAccount.ClientEmail) ?? configuredClientEmail;
            configuredPrivateKey = NullIfWhiteSpace(serviceAccount.PrivateKey) ?? configuredPrivateKey;
        }
        else if (!string.IsNullOrWhiteSpace(configuredPrivateKey) &&
                 configuredPrivateKey.StartsWith("{", StringComparison.Ordinal))
        {
            var serviceAccount = JsonSerializer.Deserialize<FirebaseServiceAccount>(configuredPrivateKey);
            if (serviceAccount is null)
            {
                throw new InvalidOperationException("Firebase service-account JSON is empty.");
            }

            configuredProjectId ??= serviceAccount.ProjectId;
            configuredClientEmail ??= serviceAccount.ClientEmail;
            configuredPrivateKey = serviceAccount.PrivateKey;
        }

        configuredPrivateKey = configuredPrivateKey?.Replace("\\n", "\n");
        if (string.IsNullOrWhiteSpace(configuredProjectId) ||
            string.IsNullOrWhiteSpace(configuredClientEmail) ||
            string.IsNullOrWhiteSpace(configuredPrivateKey))
        {
            return null;
        }

        if (!configuredPrivateKey.Contains("BEGIN PRIVATE KEY", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Firebase:PrivateKey must contain PEM key text, or configure Firebase:ServiceAccountPath with the service-account JSON file path.");
        }

        return new FirebaseCredentials(configuredProjectId, configuredClientEmail, configuredPrivateKey);
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private async Task<string> GetOAuthTokenAsync(
        string clientEmail,
        string privateKey,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_cachedOAuthToken) &&
            _cachedOAuthTokenExpiresAtUtc > DateTime.UtcNow.AddMinutes(1))
        {
            return _cachedOAuthToken;
        }

        await OAuthLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(_cachedOAuthToken) &&
                _cachedOAuthTokenExpiresAtUtc > DateTime.UtcNow.AddMinutes(1))
            {
                return _cachedOAuthToken;
            }

            var issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
            var claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
            {
                iss = clientEmail,
                scope = MessagingScope,
                aud = OAuthTokenUrl,
                iat = issuedAt,
                exp = issuedAt + 3600
            }));
            var unsignedToken = $"{header}.{claims}";

            using var rsa = RSA.Create();
            rsa.ImportFromPem(privateKey);
            var signature = rsa.SignData(
                Encoding.UTF8.GetBytes(unsignedToken),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            var assertion = $"{unsignedToken}.{Base64Url(signature)}";

            var client = _httpClientFactory.CreateClient();
            using var response = await client.PostAsync(
                OAuthTokenUrl,
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                    ["assertion"] = assertion
                }),
                cancellationToken);
            response.EnsureSuccessStatusCode();

            var tokenResponse = await response.Content.ReadFromJsonAsync<FirebaseOAuthTokenResponse>(cancellationToken);
            if (tokenResponse is null || string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
            {
                throw new InvalidOperationException("Firebase OAuth token response was empty.");
            }

            _cachedOAuthToken = tokenResponse.AccessToken;
            _cachedOAuthTokenExpiresAtUtc = DateTime.UtcNow.AddSeconds(Math.Max(60, tokenResponse.ExpiresIn - 60));
            return _cachedOAuthToken;
        }
        finally
        {
            OAuthLock.Release();
        }
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private sealed record FirebaseCredentials(string ProjectId, string ClientEmail, string PrivateKey);

    private sealed record FirebaseServiceAccount(
        [property: JsonPropertyName("project_id")] string? ProjectId,
        [property: JsonPropertyName("client_email")] string? ClientEmail,
        [property: JsonPropertyName("private_key")] string? PrivateKey);

    private sealed record FirebaseOAuthTokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("token_type")] string TokenType);
}
