using System.IdentityModel.Tokens.Jwt;
using System.Text;
using livekitmeet.Components;
using livekitmeet.Data;
using livekitmeet.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();

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
                    context.Token = context.Request.Cookies[AuthService.AccessTokenCookieName];
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
            OnChallenge = context =>
            {
                // Browser page navigations should reach the sign-in page. API callers
                // must continue to receive a standard 401 response.
                if (!context.Response.HasStarted &&
                    HttpMethods.IsGet(context.Request.Method) &&
                    !context.Request.Path.StartsWithSegments("/api"))
                {
                    context.HandleResponse();
                    context.Response.Redirect("/login");
                }

                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

await DatabaseInitializer.InitializeAsync(app.Services, app.Configuration);

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
    var result = await authService.LoginAsync(
        form["username"].ToString(),
        form["password"].ToString(),
        context.RequestAborted);

    if (!result.Success || result.Tokens is null)
    {
        return Results.Redirect("/login?error=invalid");
    }

    authService.SetAuthCookies(context, result.Tokens);
    return Results.Redirect("/");
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

app.MapPost("/api/auth/logout", async (HttpContext context, IAuthService authService) =>
{
    await authService.RevokeAsync(
        context.Request.Cookies[AuthService.AccessTokenCookieName],
        context.Request.Cookies[AuthService.RefreshTokenCookieName],
        context.RequestAborted);
    authService.ClearAuthCookies(context);
    return Results.Redirect("/login?loggedOut=1");
}).AllowAnonymous();

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

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
