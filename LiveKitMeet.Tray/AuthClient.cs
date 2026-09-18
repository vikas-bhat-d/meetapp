using System.Net.Http.Json;

namespace LiveKitMeet.Tray;

public sealed class AuthClient : IDisposable
{
    private readonly HttpClient _httpClient = new();

    public async Task<AuthTokenResponse> LoginAsync(
        string serverUrl,
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        var baseUrl = NormalizeServerUrl(serverUrl);
        using var response = await _httpClient.PostAsJsonAsync(
            $"{baseUrl}/api/auth/token",
            new { Username = username, Password = password },
            cancellationToken);
        await EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<AuthTokenResponse>(cancellationToken: cancellationToken)
               ?? throw new InvalidOperationException("The server returned an empty login response.");
    }

    public async Task<AuthTokenResponse> RefreshAsync(
        string serverUrl,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        var baseUrl = NormalizeServerUrl(serverUrl);
        using var response = await _httpClient.PostAsJsonAsync(
            $"{baseUrl}/api/auth/token/refresh",
            new { RefreshToken = refreshToken },
            cancellationToken);
        await EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<AuthTokenResponse>(cancellationToken: cancellationToken)
               ?? throw new InvalidOperationException("The server returned an empty refresh response.");
    }

    public async Task<bool> ReportCallOutcomeAsync(
        string serverUrl,
        string accessToken,
        Guid invitationId,
        string outcome,
        CancellationToken cancellationToken = default)
    {
        var baseUrl = NormalizeServerUrl(serverUrl);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{baseUrl}/api/call-invitations/{invitationId:D}/{Uri.EscapeDataString(outcome)}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public static string NormalizeServerUrl(string serverUrl)
    {
        if (!Uri.TryCreate(serverUrl.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("Enter a valid HTTP or HTTPS server URL.");
        }

        return uri.ToString().TrimEnd('/');
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var message = await response.Content.ReadAsStringAsync();
        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(message)
                ? $"The server returned HTTP {(int)response.StatusCode}."
                : message);
    }

    public void Dispose() => _httpClient.Dispose();
}
