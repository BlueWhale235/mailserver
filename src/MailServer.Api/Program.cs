using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using MailServer.Api;
using MailServer.Domain;
using MailServer.Infrastructure;
using MailServer.Imap;
using MailServer.Smtp;
using MailServer.Worker;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddPortableConfiguration();
var adminCertificate = AdminWebHosting.ConfigureAdminListeners(builder);
builder.Services.AddMailServerInfrastructure(builder.Configuration);
var runAll = builder.Configuration.GetValue("RunAll:Enabled", false);
builder.Services.AddMailServerPlugins(builder.Configuration, runAll ? "all" : "api");
builder.Services.AddMailServerAdminWeb(builder.Configuration);
if (runAll)
{
    builder.Services.Configure<SmtpServerOptions>(builder.Configuration.GetSection(SmtpServerOptions.SectionName));
    builder.Services.Configure<ImapServerOptions>(builder.Configuration.GetSection(ImapServerOptions.SectionName));
    builder.Services.AddSingleton<BounceService>();
    builder.Services.AddHostedService<SmtpServerService>();
    builder.Services.AddHostedService<ImapServerService>();
    builder.Services.AddHostedService<DeliveryWorker>();
}
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "mailserver";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "mailserver-admin";
var jwtSigningKey = builder.Configuration["Jwt:SigningKey"]
    ?? throw new InvalidOperationException("Jwt:SigningKey is required.");
if (Encoding.UTF8.GetByteCount(jwtSigningKey) < 32)
{
    throw new InvalidOperationException("Jwt:SigningKey must contain at least 32 bytes.");
}

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = "smart";
        options.DefaultChallengeScheme = "smart";
    })
    .AddPolicyScheme("smart", "JWT or API key", options =>
    {
        options.ForwardDefaultSelector = context =>
            context.Request.Headers.ContainsKey("X-Api-Key")
                ? ApiKeyAuthenticationHandler.SchemeName
                : context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    ? JwtBearerDefaults.AuthenticationScheme
                    : AdminWebHosting.CookieScheme;
    })
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    })
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationHandler.SchemeName,
        _ => { })
    .AddAdminCookie();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("admin", policy => policy.RequireAuthenticatedUser().RequireClaim("scope", "admin"));
});

var app = builder.Build();
await app.Services.VerifyMailDatabaseAsync(app.Lifetime.ApplicationStopping);
await BootstrapAdminAsync(app.Services, app.Configuration, app.Logger, app.Lifetime.ApplicationStopping);

app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/_framework/blazor.web.js", () =>
{
    var stream = typeof(MailServer.Api.Components.App).Assembly
        .GetManifestResourceStream("MailServer.Api.blazor.web.js");
    return stream is null
        ? Results.NotFound()
        : Results.Stream(stream, "text/javascript; charset=utf-8");
}).AllowAnonymous();
app.MapMailServerAdminWeb(adminCertificate);

app.MapGet("/api/v1/health", async (IDbContextFactory<MailDbContext> factory, CancellationToken cancellationToken) =>
{
    await using var db = await factory.CreateDbContextAsync(cancellationToken);
    var database = await db.Database.CanConnectAsync(cancellationToken);
    return database ? Results.Ok(new { status = "healthy" }) : Results.Problem("Database unavailable.");
}).AllowAnonymous();

app.MapPost("/api/v1/auth/token", async (
    LoginRequest request,
    AdminApplicationService administration,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var admin = await administration.AuthenticateAsync(request.UserName, request.Password, cancellationToken);
    if (admin is null)
    {
        return Results.Unauthorized();
    }

    var now = DateTimeOffset.UtcNow;
    var expires = now.AddMinutes(configuration.GetValue("Jwt:Minutes", 15));
    var token = new JwtSecurityToken(
        issuer: jwtIssuer,
        audience: jwtAudience,
        claims:
        [
            new Claim(JwtRegisteredClaimNames.Sub, admin.Id.ToString()),
            new Claim(ClaimTypes.Name, admin.UserName),
            new Claim("scope", "admin")
        ],
        notBefore: now.UtcDateTime,
        expires: expires.UtcDateTime,
        signingCredentials: new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSigningKey)),
            SecurityAlgorithms.HmacSha256));
    return Results.Ok(new
    {
        accessToken = new JwtSecurityTokenHandler().WriteToken(token),
        tokenType = "Bearer",
        expiresAt = expires
    });
}).AllowAnonymous();

var api = app.MapGroup("/api/v1").RequireAuthorization("admin");

api.MapGet("/domains", async (AdminApplicationService administration, CancellationToken cancellationToken) =>
    Results.Ok(await administration.GetDomainsAsync(cancellationToken)));

api.MapPost("/domains", async (
    DomainRequest request,
    HttpContext http,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    try
    {
        var domain = await administration.CreateDomainAsync(
            request.Name,
            request.OutboundMode,
            request.DkimSelector,
            request.DkimPrivateKeyPath,
            Actor(http),
            cancellationToken);
        return Results.Created($"/api/v1/domains/{domain.Id}", domain);
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { error = exception.Message });
    }
});

api.MapPatch("/domains/{id:guid}", async (
    Guid id,
    UpdateDomainRequest request,
    HttpContext http,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    try
    {
        var domain = await administration.UpdateDomainAsync(
            id,
            request.IsActive,
            request.OutboundMode,
            request.DkimSelector,
            request.DkimPrivateKeyPath,
            Actor(http),
            cancellationToken);
        return Results.Ok(domain);
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
});

api.MapGet("/accounts", async (AdminApplicationService administration, CancellationToken cancellationToken) =>
    Results.Ok(await administration.GetAccountsAsync(cancellationToken)));

api.MapPost("/accounts", async (
    AccountRequest request,
    HttpContext http,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    try
    {
        var account = await administration.CreateAccountAsync(
            request.Address,
            request.Password,
            request.QuotaBytes,
            Actor(http),
            cancellationToken);
        return Results.Created($"/api/v1/accounts/{account.Id}", account);
    }
    catch (FormatException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { error = exception.Message });
    }
});

api.MapPost("/accounts/{id:guid}/password", async (
    Guid id,
    PasswordRequest request,
    HttpContext http,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    try
    {
        await administration.SetAccountPasswordAsync(id, request.Password, Actor(http), cancellationToken);
        return Results.NoContent();
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (FormatException exception) { return Results.BadRequest(new { error = exception.Message }); }
});

api.MapPatch("/accounts/{id:guid}", async (
    Guid id,
    UpdateAccountRequest request,
    HttpContext http,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    try
    {
        await administration.UpdateAccountAsync(id, request.IsEnabled, request.QuotaBytes, Actor(http), cancellationToken);
        return Results.NoContent();
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
});

api.MapGet("/aliases", async (AdminApplicationService administration, CancellationToken cancellationToken) =>
    Results.Ok(await administration.GetAliasesAsync(cancellationToken)));

api.MapPost("/aliases", async (
    AliasRequest request,
    HttpContext http,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    try
    {
        var alias = await administration.CreateAliasAsync(
            request.Source,
            request.Destination,
            Actor(http),
            cancellationToken);
        return Results.Created($"/api/v1/aliases/{alias.Id}", alias);
    }
    catch (Exception exception) when (exception is FormatException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
});

api.MapDelete("/aliases/{id:guid}", async (
    Guid id,
    HttpContext http,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    try
    {
        await administration.DeleteAliasAsync(id, Actor(http), cancellationToken);
        return Results.NoContent();
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
});

api.MapPost("/api-keys", async (
    ApiKeyRequest request,
    HttpContext http,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    var key = await administration.CreateApiKeyAsync(
        request.Name,
        request.Scopes,
        request.ExpiresAt,
        Actor(http),
        cancellationToken);
    return Results.Ok(new { key.Id, apiKey = key.Value, key.ExpiresAt });
});

api.MapGet("/api-keys", async (
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
    Results.Ok(await administration.GetApiKeysAsync(cancellationToken)));

api.MapDelete("/api-keys/{id:guid}", async (
    Guid id,
    HttpContext http,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    try
    {
        await administration.RevokeApiKeyAsync(id, Actor(http), cancellationToken);
        return Results.NoContent();
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
});

api.MapPost("/admins/me/password", async (
    ChangeAdminPasswordRequest request,
    HttpContext http,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    var subject = http.User.FindFirstValue(JwtRegisteredClaimNames.Sub)
                  ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (!Guid.TryParse(subject, out var adminId)) return Results.Unauthorized();
    try
    {
        await administration.ChangeAdminPasswordAsync(
            adminId,
            request.CurrentPassword,
            request.NewPassword,
            true,
            Actor(http),
            cancellationToken);
        return Results.NoContent();
    }
    catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
    catch (FormatException exception) { return Results.BadRequest(new { error = exception.Message }); }
});

api.MapGet("/queues", async (
    DeliveryStatus? status,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
    Results.Ok(await administration.GetDeliveryJobsAsync(status, cancellationToken)));

api.MapPost("/queues/{id:guid}/retry", async (
    Guid id,
    HttpContext http,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    await administration.RetryDeliveryAsync(id, Actor(http), cancellationToken);
    return Results.Accepted();
});

api.MapGet("/diagnostics/dns", async (
    string domain,
    string? selector,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
    Results.Ok(await administration.DiagnoseDnsAsync(domain, selector, cancellationToken)));

app.Run();

static async Task BootstrapAdminAsync(
    IServiceProvider services,
    IConfiguration configuration,
    ILogger logger,
    CancellationToken cancellationToken)
{
    await using var scope = services.CreateAsyncScope();
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MailDbContext>>();
    var credentials = scope.ServiceProvider.GetRequiredService<ICredentialService>();
    await using var db = await factory.CreateDbContextAsync(cancellationToken);
    if (await db.AdminUsers.AnyAsync(cancellationToken)) return;
    var userName = (configuration["Bootstrap:AdminUserName"] ?? "admin").Trim().ToLowerInvariant();
    var password = configuration["Bootstrap:AdminPassword"]
        ?? throw new InvalidOperationException("Bootstrap:AdminPassword is required for the first startup.");
    if (!IsStrongPassword(password))
        throw new InvalidOperationException("Bootstrap:AdminPassword must be at least 12 characters.");
    db.AdminUsers.Add(new AdminUser { UserName = userName, PasswordHash = credentials.HashPassword(password) });
    await db.SaveChangesAsync(cancellationToken);
    logger.LogWarning("Created bootstrap administrator {UserName}; rotate its password and configuration secret.", userName);
}

static string Actor(HttpContext http) => http.User.Identity?.Name ?? "unknown";

static bool IsStrongPassword(string password) => !string.IsNullOrWhiteSpace(password) && password.Length >= 12;

public sealed record LoginRequest(string UserName, string Password);
public sealed record DomainRequest(string Name, OutboundMode OutboundMode, string? DkimSelector, string? DkimPrivateKeyPath);
public sealed record UpdateDomainRequest(bool? IsActive, OutboundMode? OutboundMode, string? DkimSelector, string? DkimPrivateKeyPath);
public sealed record AccountRequest(string Address, string Password, long? QuotaBytes);
public sealed record PasswordRequest(string Password);
public sealed record UpdateAccountRequest(bool? IsEnabled, long? QuotaBytes);
public sealed record AliasRequest(string Source, string Destination);
public sealed record ApiKeyRequest(string Name, string[] Scopes, DateTimeOffset? ExpiresAt);
public sealed record ChangeAdminPasswordRequest(string CurrentPassword, string NewPassword);

public partial class Program;
