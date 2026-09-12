using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using livekitmeet.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace livekitmeet.Services;

public sealed record IssuedAuthTokens(
    string AccessToken,
    DateTime AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTime RefreshTokenExpiresAtUtc);

public sealed record AuthenticationResult(bool Success, string? Error, IssuedAuthTokens? Tokens)
{
    public static AuthenticationResult Failed(string error) => new(false, error, null);
}

public interface IAuthService
{
    Task<AuthenticationResult> LoginAsync(string userName, string password, CancellationToken cancellationToken = default);
    Task<AuthenticationResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default);
    Task<bool> ValidateAccessTokenAsync(string accessToken, ClaimsPrincipal principal, CancellationToken cancellationToken = default);
    Task RevokeAsync(string? accessToken, string? refreshToken, CancellationToken cancellationToken = default);
    Task<(bool Success, string? Error)> ChangePasswordAsync(
        ClaimsPrincipal principal,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default);
    void SetAuthCookies(HttpContext context, IssuedAuthTokens tokens);
    void ClearAuthCookies(HttpContext context);
}

public sealed class AuthService : IAuthService
{
    public const string AccessTokenCookieName = "livekit_access_token";
    public const string RefreshTokenCookieName = "livekit_refresh_token";
    private const string Issuer = "livekitmeet";
    private const string Audience = "livekitmeet";

    private readonly AppDbContext _db;
    private readonly IPasswordHasher<AppUser> _passwordHasher;
    private readonly IConfiguration _configuration;
    private readonly JwtSecurityTokenHandler _tokenHandler = new();

    public AuthService(
        AppDbContext db,
        IPasswordHasher<AppUser> passwordHasher,
        IConfiguration configuration)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _configuration = configuration;
    }

    public async Task<AuthenticationResult> LoginAsync(
        string userName,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
        {
            return AuthenticationResult.Failed("Username and password are required.");
        }

        var normalized = UserNameNormalizer.Normalize(userName);
        var user = await _db.Users.SingleOrDefaultAsync(
            candidate => candidate.NormalizedUserName == normalized,
            cancellationToken);

        if (user is null || !user.IsActive)
        {
            return AuthenticationResult.Failed("Invalid username or password.");
        }

        var verification = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (verification == PasswordVerificationResult.Failed)
        {
            return AuthenticationResult.Failed("Invalid username or password.");
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = _passwordHasher.HashPassword(user, password);
        }

        user.LastLoginAtUtc = DateTime.UtcNow;
        var tokens = CreateTokens(user);
        _db.AuthSessions.Add(tokens.Session);
        await _db.SaveChangesAsync(cancellationToken);
        return new AuthenticationResult(true, null, tokens.Issued);
    }

    public async Task<AuthenticationResult> RefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return AuthenticationResult.Failed("Refresh token is missing.");
        }

        var tokenHash = HashToken(refreshToken);
        var session = await _db.AuthSessions
            .Include(candidate => candidate.User)
            .SingleOrDefaultAsync(candidate => candidate.RefreshTokenHash == tokenHash, cancellationToken);

        if (session is null || !IsSessionRefreshable(session))
        {
            return AuthenticationResult.Failed("Refresh token is invalid or expired.");
        }

        session.RevokedAtUtc = DateTime.UtcNow;
        var tokens = CreateTokens(session.User);
        _db.AuthSessions.Add(tokens.Session);
        await _db.SaveChangesAsync(cancellationToken);
        return new AuthenticationResult(true, null, tokens.Issued);
    }

    public async Task<bool> ValidateAccessTokenAsync(
        string accessToken,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken) || principal.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var session = await _db.AuthSessions
            .AsNoTracking()
            .Include(candidate => candidate.User)
            .SingleOrDefaultAsync(
                candidate => candidate.AccessTokenHash == HashToken(accessToken),
                cancellationToken);

        if (session is null || session.RevokedAtUtc is not null || !session.User.IsActive ||
            session.AccessTokenExpiresAtUtc <= DateTime.UtcNow)
        {
            return false;
        }

        var subject = principal.FindFirstValue(ClaimTypes.NameIdentifier) ??
                      principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
        var tokenId = principal.FindFirstValue(JwtRegisteredClaimNames.Jti);
        return Guid.TryParse(subject, out var userId) && userId == session.UserId &&
               Guid.TryParse(tokenId, out var sessionId) && sessionId == session.Id;
    }

    public async Task RevokeAsync(
        string? accessToken,
        string? refreshToken,
        CancellationToken cancellationToken = default)
    {
        var hashes = new[] { accessToken, refreshToken }
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Select(token => HashToken(token!))
            .ToArray();

        if (hashes.Length == 0)
        {
            return;
        }

        var sessions = await _db.AuthSessions
            .Where(session => hashes.Contains(session.AccessTokenHash) || hashes.Contains(session.RefreshTokenHash))
            .ToListAsync(cancellationToken);

        foreach (var session in sessions)
        {
            session.RevokedAtUtc = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<(bool Success, string? Error)> ChangePasswordAsync(
        ClaimsPrincipal principal,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        if (newPassword.Length < 8)
        {
            return (false, "The new password must be at least 8 characters.");
        }

        var userIdValue = principal.FindFirstValue(ClaimTypes.NameIdentifier) ??
                          principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (!Guid.TryParse(userIdValue, out var userId))
        {
            return (false, "The authenticated user could not be identified.");
        }

        var user = await _db.Users
            .Include(candidate => candidate.AuthSessions)
            .SingleOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken);
        if (user is null || !user.IsActive)
        {
            return (false, "The user account is not active.");
        }

        var verification = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, currentPassword);
        if (verification == PasswordVerificationResult.Failed)
        {
            return (false, "The current password is incorrect.");
        }

        user.PasswordHash = _passwordHasher.HashPassword(user, newPassword);
        foreach (var session in user.AuthSessions)
        {
            session.RevokedAtUtc = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    public void SetAuthCookies(HttpContext context, IssuedAuthTokens tokens)
    {
        var secure = context.Request.IsHttps;
        context.Response.Cookies.Append(AccessTokenCookieName, tokens.AccessToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            SameSite = SameSiteMode.Lax,
            Expires = new DateTimeOffset(tokens.AccessTokenExpiresAtUtc),
            IsEssential = true,
            Path = "/"
        });
        context.Response.Cookies.Append(RefreshTokenCookieName, tokens.RefreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            SameSite = SameSiteMode.Lax,
            Expires = new DateTimeOffset(tokens.RefreshTokenExpiresAtUtc),
            IsEssential = true,
            Path = "/api/auth"
        });
    }

    public void ClearAuthCookies(HttpContext context)
    {
        context.Response.Cookies.Delete(AccessTokenCookieName, new CookieOptions { Path = "/" });
        context.Response.Cookies.Delete(RefreshTokenCookieName, new CookieOptions { Path = "/api/auth" });
    }

    private (IssuedAuthTokens Issued, AuthSession Session) CreateTokens(AppUser user)
    {
        var now = DateTime.UtcNow;
        var accessExpires = now.AddMinutes(GetPositiveSetting("Auth:AccessTokenMinutes", 30));
        var refreshExpires = now.AddDays(GetPositiveSetting("Auth:RefreshTokenDays", 14));
        var sessionId = Guid.NewGuid();
        var refreshToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.UserName),
            new Claim("display_name", user.DisplayName),
            new Claim(ClaimTypes.Role, user.IsAdmin ? "Admin" : "User"),
            new Claim(JwtRegisteredClaimNames.Jti, sessionId.ToString())
        };
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = accessExpires,
            NotBefore = now,
            IssuedAt = now,
            Issuer = Issuer,
            Audience = Audience,
            SigningCredentials = new SigningCredentials(GetSigningKey(), SecurityAlgorithms.HmacSha256)
        };
        var accessToken = _tokenHandler.WriteToken(_tokenHandler.CreateToken(descriptor));
        var issued = new IssuedAuthTokens(accessToken, accessExpires, refreshToken, refreshExpires);
        var session = new AuthSession
        {
            Id = sessionId,
            UserId = user.Id,
            AccessTokenHash = HashToken(accessToken),
            AccessTokenExpiresAtUtc = accessExpires,
            RefreshTokenHash = HashToken(refreshToken),
            RefreshTokenExpiresAtUtc = refreshExpires
        };
        return (issued, session);
    }

    private bool IsSessionRefreshable(AuthSession session) =>
        session.RevokedAtUtc is null && session.User.IsActive &&
        session.RefreshTokenExpiresAtUtc > DateTime.UtcNow;

    private SymmetricSecurityKey GetSigningKey()
    {
        var secret = _configuration["Auth:JwtSecret"];
        if (string.IsNullOrWhiteSpace(secret) || Encoding.UTF8.GetByteCount(secret) < 32)
        {
            throw new InvalidOperationException("Auth:JwtSecret must be configured with at least 32 bytes.");
        }

        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
    }

    private int GetPositiveSetting(string key, int fallback)
    {
        return int.TryParse(_configuration[key], out var value) && value > 0 ? value : fallback;
    }

    private static string HashToken(string token)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }
}
