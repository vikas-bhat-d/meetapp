namespace livekitmeet.Services;

public sealed record AuthTokenLoginRequest(string Username, string Password);

public sealed record AuthTokenRefreshRequest(string RefreshToken);
