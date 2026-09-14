using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using livekitmeet.Components;
using livekitmeet.Data;
using livekitmeet.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

static string GetSafeReturnUrl(string? returnUrl)
{
    if (string.IsNullOrWhiteSpace(returnUrl) ||
        !returnUrl.StartsWith("/", StringComparison.Ordinal) ||
        returnUrl.StartsWith("//", StringComparison.Ordinal) ||
        returnUrl.Contains('\\') ||
        returnUrl.Any(char.IsControl))
    {
        return "/";
    }

    return returnUrl;
}

static string BuildLoginRedirect(string? returnUrl, string? error = null)
{
    var query = new List<string>();
    if (!string.IsNullOrWhiteSpace(error))
    {
        query.Add($"error={Uri.EscapeDataString(error)}");
    }

    var safeReturnUrl = GetSafeReturnUrl(returnUrl);
    if (safeReturnUrl != "/")
    {
        query.Add($"returnUrl={Uri.EscapeDataString(safeReturnUrl)}");
    }

    return query.Count == 0 ? "/login" : $"/login?{string.Join('&', query)}";
}

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSignalR();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownProxies.Add(IPAddress.Loopback);
});

var databaseProvider = builder.Configuration["Database:Provider"]?.Trim().ToLowerInvariant() ?? "sqlite";
var connectionString = builder.Configuration["Database:ConnectionString"] ??
                       builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("Database:ConnectionString must be configured.");
}

builder.Services.AddDbContext<AppDbContext>(options =>
{
    switch (databaseProvider)
    {
        case "sqlite":
            options.UseSqlite(connectionString);
            break;
        case "sqlserver":
        case "mssql":
            options.UseSqlServer(connectionString);
            break;
        default:
            throw new InvalidOperationException(
                $"Unsupported database provider '{databaseProvider}'. Use 'Sqlite' or 'SqlServer'.");
    }
});

builder.Services.AddSingleton<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IUserAdministrationService, UserAdministrationService>();
builder.Services.AddSingleton<CallInvitationConnectionTracker>();
builder.Services.AddScoped<ICallInvitationService, CallInvitationService>();
builder.Services.AddScoped<IFirebasePushNotificationService, FirebasePushNotificationService>();
builder.Services.AddSingleton<ILiveKitTokenService, LiveKitTokenService>();

var jwtSecret = builder.Configuration["Auth:JwtSecret"];
if (string.IsNullOrWhiteSpace(jwtSecret) || Encoding.UTF8.GetByteCount(jwtSecret) < 32)
{
    throw new InvalidOperationException("Auth:JwtSecret must be configured with at least 32 bytes.");
}

builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ValidateIssuer = true,
            ValidIssuer = "livekitmeet",
            ValidateAudience = true,
            ValidAudience = "livekitmeet",
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = System.Security.Claims.ClaimTypes.Name,
            RoleClaimType = System.Security.Claims.ClaimTypes.Role
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (string.IsNullOrWhiteSpace(context.Token))
                {
                    var authorization = context.Request.Headers.Authorization.ToString();
                    if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    {
                        context.Token = authorization["Bearer ".Length..].Trim();
                    }
                }

                if (string.IsNullOrWhiteSpace(context.Token))
                {
                    context.Token = context.Request.Cookies[AuthService.AccessTokenCookieName];
                }

                if (string.IsNullOrWhiteSpace(context.Token) &&
                    context.Request.Path.StartsWithSegments("/hubs/call-invitations"))
                {
                    context.Token = context.Request.Query["access_token"];
                }

                if (!string.IsNullOrWhiteSpace(context.Token))
                {
                    context.HttpContext.Items["livekit.raw_access_token"] = context.Token;
                }

                return Task.CompletedTask;
            },
            OnTokenValidated = async context =>
            {
                var accessToken = context.HttpContext.Items["livekit.raw_access_token"] as string;
                var principal = context.Principal;
                if (string.IsNullOrWhiteSpace(accessToken) || principal is null)
                {
                    context.Fail("The access token is missing.");
                    return;
                }

                var authService = context.HttpContext.RequestServices.GetRequiredService<IAuthService>();
                if (!await authService.ValidateAccessTokenAsync(accessToken, principal, context.HttpContext.RequestAborted))
                {
                    context.Fail("The access token has been revoked or is no longer valid.");
                }
            },
            OnChallenge = async context =>
            {
                // Browser page navigations should reach the sign-in page. API callers
                // must continue to receive a standard 401 response.
                if (!context.Response.HasStarted &&
                    HttpMethods.IsGet(context.Request.Method) &&
                    !context.Request.Path.StartsWithSegments("/api"))
                {
                    var refreshToken = context.Request.Cookies[AuthService.RefreshTokenCookieName];
                    if (!string.IsNullOrWhiteSpace(refreshToken))
                    {
                        var authService = context.HttpContext.RequestServices.GetRequiredService<IAuthService>();
                        var refreshResult = await authService.RefreshAsync(
                            refreshToken,
                            context.HttpContext.RequestAborted);
                        if (refreshResult.Success && refreshResult.Tokens is not null)
                        {
                            authService.SetAuthCookies(context.HttpContext, refreshResult.Tokens);
                            context.HandleResponse();
                            var currentUrl = $"{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}";
                            context.Response.Redirect(GetSafeReturnUrl(currentUrl));
                            return;
                        }

                        authService.ClearAuthCookies(context.HttpContext);
                    }

                    context.HandleResponse();
                    var returnUrl = $"{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}";
                    context.Response.Redirect(BuildLoginRedirect(returnUrl));
                }
            }
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

await DatabaseInitializer.InitializeAsync(app.Services, app.Configuration);

// Caddy terminates HTTPS and forwards traffic to the local ASP.NET process.
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapPost("/api/auth/login", async (HttpContext context, IAuthService authService) =>
{
    var form = await context.Request.ReadFormAsync(context.RequestAborted);
    var returnUrl = GetSafeReturnUrl(form["returnUrl"].ToString());
    var result = await authService.LoginAsync(
        form["username"].ToString(),
        form["password"].ToString(),
        context.RequestAborted);

    if (!result.Success || result.Tokens is null)
    {
        return Results.Redirect(BuildLoginRedirect(returnUrl, "invalid"));
    }

    authService.SetAuthCookies(context, result.Tokens);
    return Results.Redirect(returnUrl);
}).AllowAnonymous();

app.MapPost("/api/auth/token", async (AuthTokenLoginRequest request, IAuthService authService) =>
{
    var result = await authService.LoginAsync(request.Username, request.Password);
    if (!result.Success || result.Tokens is null)
    {
        return Results.Unauthorized();
    }

    return Results.Ok(result.Tokens);
}).AllowAnonymous();

app.MapPost("/api/auth/refresh", async (HttpContext context, IAuthService authService) =>
{
    var refreshToken = context.Request.Cookies[AuthService.RefreshTokenCookieName];
    var result = await authService.RefreshAsync(refreshToken ?? string.Empty, context.RequestAborted);
    if (!result.Success || result.Tokens is null)
    {
        authService.ClearAuthCookies(context);
        return Results.Unauthorized();
    }

    authService.SetAuthCookies(context, result.Tokens);
    return Results.NoContent();
}).AllowAnonymous();

app.MapPost("/api/auth/token/refresh", async (AuthTokenRefreshRequest request, IAuthService authService) =>
{
    var result = await authService.RefreshAsync(request.RefreshToken);
    if (!result.Success || result.Tokens is null)
    {
        return Results.Unauthorized();
    }

    return Results.Ok(result.Tokens);
}).AllowAnonymous();

app.MapPost("/api/auth/logout", async (HttpContext context, IAuthService authService) =>
{
    await authService.RevokeAsync(
        context.Request.Cookies[AuthService.AccessTokenCookieName],
        context.Request.Cookies[AuthService.RefreshTokenCookieName],
        context.RequestAborted);
    authService.ClearAuthCookies(context);
    return Results.Redirect("/login?loggedOut=1");
}).AllowAnonymous();

app.MapPost("/api/devices/fcm", async (
    HttpContext context,
    PushDeviceRegistrationRequest request,
    AppDbContext db) =>
{
    var userIdValue = context.User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);
    if (!Guid.TryParse(userIdValue, out var userId))
    {
        return Results.Unauthorized();
    }

    var token = request.Token?.Trim();
    var platform = request.Platform?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(token) || token.Length > 4096 || platform != "android")
    {
        return Results.BadRequest(new { error = "A valid Android push token is required." });
    }

    var device = await db.PushDevices.SingleOrDefaultAsync(
        candidate => candidate.PushToken == token,
        context.RequestAborted);
    if (device is null)
    {
        device = new PushDevice
        {
            UserId = userId,
            Platform = platform,
            PushToken = token
        };
        db.PushDevices.Add(device);
    }
    else
    {
        device.UserId = userId;
        device.Platform = platform;
        device.LastSeenAtUtc = DateTime.UtcNow;
        device.IsActive = true;
    }

    await db.SaveChangesAsync(context.RequestAborted);
    return Results.NoContent();
}).RequireAuthorization();

app.MapPost("/api/devices/fcm/unregister", async (
    HttpContext context,
    PushDeviceRegistrationRequest request,
    AppDbContext db) =>
{
    var userIdValue = context.User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);
    if (!Guid.TryParse(userIdValue, out var userId))
    {
        return Results.Unauthorized();
    }

    var token = request.Token?.Trim();
    if (!string.IsNullOrWhiteSpace(token))
    {
        var devices = await db.PushDevices
            .Where(device => device.UserId == userId && device.PushToken == token)
            .ToListAsync(context.RequestAborted);
        foreach (var device in devices)
        {
            device.IsActive = false;
        }

        await db.SaveChangesAsync(context.RequestAborted);
    }

    return Results.NoContent();
}).RequireAuthorization();

app.MapPost("/api/auth/change-password", async (HttpContext context, IAuthService authService) =>
{
    var form = await context.Request.ReadFormAsync(context.RequestAborted);
    var result = await authService.ChangePasswordAsync(
        context.User,
        form["currentPassword"].ToString(),
        form["newPassword"].ToString(),
        context.RequestAborted);

    if (!result.Success)
    {
        var error = Uri.EscapeDataString(result.Error ?? "Unable to change password.");
        return Results.Redirect($"/profile?error={error}");
    }

    authService.ClearAuthCookies(context);
    return Results.Redirect("/login?changed=1");
}).RequireAuthorization();

app.MapGet("/api/connection-details", (
    HttpContext context,
    string? roomName,
    string? metadata,
    ILiveKitTokenService tokenService) =>
{
    if (context.User.Identity?.IsAuthenticated != true)
    {
        return Results.Unauthorized();
    }
    if (string.IsNullOrWhiteSpace(roomName))
    {
        return Results.BadRequest(new { error = "Missing required query parameter: roomName" });
    }
    try
    {
        var details = tokenService.CreateConnectionDetails(roomName.Trim(), context.User, metadata);
        return Results.Ok(details);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
}).RequireAuthorization();

app.MapHub<CallInvitationHub>("/hubs/call-invitations").RequireAuthorization();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
