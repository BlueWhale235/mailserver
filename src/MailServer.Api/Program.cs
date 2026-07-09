using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MailServer.Api;
using MailServer.Domain;
using MailServer.Infrastructure;
using MailServer.Imap;
using MailServer.Smtp;
using MailServer.Worker;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddPortableConfiguration();
builder.Logging.AddMailServerLogging(builder.Configuration);
var adminCertificate = AdminWebHosting.ConfigureAdminListeners(builder);
builder.Services.AddMailServerInfrastructure(builder.Configuration);
var runAll = builder.Configuration.GetValue("RunAll:Enabled", false);
builder.Services.AddMailServerPlugins(builder.Configuration, runAll ? "all" : "api");
builder.Services.AddMailServerAdminWeb(builder.Configuration);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "MailServer API",
        Version = "v1"
    });
    options.DocInclusionPredicate((_, description) =>
        description.RelativePath?.StartsWith("api/v1", StringComparison.OrdinalIgnoreCase) == true);
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "输入后台登录接口返回的 JWT Bearer Token。Bearer 和 X-Api-Key 只能选择一种。"
    });
    options.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        Name = "X-Api-Key",
        In = ParameterLocation.Header,
        Description = "输入在 API 管理中创建的 API Key。Bearer 和 X-Api-Key 只能选择一种。"
    });
    options.OperationFilter<SwaggerEndpointDescriptionOperationFilter>();
    options.OperationFilter<SwaggerAuthorizationOperationFilter>();
});
if (runAll)
{
    builder.Services.Configure<SmtpServerOptions>(builder.Configuration.GetSection(SmtpServerOptions.SectionName));
    builder.Services.Configure<SmtpAbuseOptions>(builder.Configuration.GetSection(SmtpAbuseOptions.SectionName));
    builder.Services.Configure<ImapServerOptions>(builder.Configuration.GetSection(ImapServerOptions.SectionName));
    builder.Services.AddSingleton<BounceService>();
    builder.Services.AddHostedService<SmtpServerService>();
    builder.Services.AddHostedService<ImapServerService>();
    builder.Services.AddHostedService<DeliveryWorker>();
    builder.Services.AddHostedService<SentCopyBackgroundService>();
    builder.Services.AddHostedService<TlsReportBackgroundService>();
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
            context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? JwtBearerDefaults.AuthenticationScheme
                : context.Request.Headers.ContainsKey("X-Api-Key")
                    ? ApiKeyAuthenticationHandler.SchemeName
                    : context.Request.Path.StartsWithSegments("/api")
                        ? JwtBearerDefaults.AuthenticationScheme
                        : AdminWebHosting.CookieScheme;
    })
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = ClaimTypes.Name
        };
    })
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationHandler.SchemeName,
        _ => { })
    .AddAdminCookie();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("admin", policy => policy.RequireAuthenticatedUser());
});

var app = builder.Build();

app.MapGet("/.well-known/mta-sts.txt", async (
    HttpContext context,
    IDbContextFactory<MailDbContext> factory,
    CancellationToken cancellationToken) =>
{
    var host = context.Request.Host.Host.TrimEnd('.').ToLowerInvariant();
    if (!host.StartsWith("mta-sts.", StringComparison.Ordinal))
        return Results.NotFound();
    var domainName = host["mta-sts.".Length..];
    await using var db = await factory.CreateDbContextAsync(cancellationToken);
    var domain = await db.Domains.AsNoTracking()
        .Include(x => x.DnsSettings)
        .SingleOrDefaultAsync(x => x.Name == domainName && x.IsActive, cancellationToken);
    var settings = domain?.DnsSettings;
    if (settings is null || settings.MtaStsMode == "none")
        return Results.NotFound();
    var mx = settings.MtaStsMxPatterns
        .Split([',', ';', '\r', '\n', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var policyLines = new List<string>
    {
        "version: STSv1",
        $"mode: {settings.MtaStsMode}"
    };
    policyLines.AddRange(mx.Select(value => $"mx: {value}"));
    policyLines.Add($"max_age: {settings.MtaStsMaxAgeSeconds}");
    policyLines.Add(string.Empty);
    var policy = string.Join("\r\n", policyLines);
    return Results.Text(policy, "text/plain; charset=utf-8");
}).AllowAnonymous();
if (!builder.Configuration.GetValue("DesignTime:Enabled", false))
{
    await app.Services.VerifyMailDatabaseAsync(app.Lifetime.ApplicationStopping);
    await BootstrapAdminAsync(app.Services, app.Configuration, app.Logger, app.Lifetime.ApplicationStopping);
}

app.ExtractBundledStaticWebAssets();
app.UseStaticFiles();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api")
        && !string.IsNullOrWhiteSpace(context.Request.Headers.Authorization.ToString())
        && !string.IsNullOrWhiteSpace(context.Request.Headers["X-Api-Key"].ToString()))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new
        {
            error = "Authorization and X-Api-Key cannot be used together. Choose one authentication method."
        });
        return;
    }

    await next();
});
app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/swagger")
        && context.User.Identity?.IsAuthenticated != true)
    {
        await context.ChallengeAsync(AdminWebHosting.CookieScheme);
        return;
    }

    await next();
});
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "MailServer API v1");
    options.RoutePrefix = "swagger";
    options.DocumentTitle = "MailServer API";
});
app.MapGet("/_framework/blazor.web.js", () =>
{
    var stream = typeof(MailServer.Api.Components.App).Assembly
        .GetManifestResourceStream("MailServer.Api.blazor.web.js");
    return stream is null
        ? Results.NotFound()
        : Results.Stream(stream, "text/javascript; charset=utf-8");
}).AllowAnonymous();
app.MapGet("/admin.css", () =>
{
    var stream = typeof(MailServer.Api.Components.App).Assembly
        .GetManifestResourceStream("MailServer.Api.admin.css");
    return stream is null
        ? Results.NotFound()
        : Results.Stream(stream, "text/css; charset=utf-8");
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
            new Claim(ClaimTypes.NameIdentifier, admin.Id.ToString()),
            new Claim(ClaimTypes.Name, admin.UserName),
            new Claim("auth_type", "admin")
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

api.MapGet("/firewall/rules", async (
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
    Results.Ok(await administration.GetFirewallRulesAsync(cancellationToken)))
    .RequirePermission(PermissionCodes.FirewallList);

api.MapPost("/firewall/rules", async (
    FirewallRuleRequest request,
    HttpContext http,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    try
    {
        var rule = await administration.CreateFirewallRuleAsync(
            request.Cidr,
            request.Note,
            Actor(http),
            cancellationToken);
        return Results.Created($"/api/v1/firewall/rules/{rule.Id}", rule);
    }
    catch (Exception exception) when (exception is FormatException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequirePermission(PermissionCodes.FirewallCreate);

api.MapPut("/firewall/rules/{id:guid}", async (
    Guid id,
    UpdateFirewallRuleRequest request,
    HttpContext http,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await administration.UpdateFirewallRuleAsync(
            id,
            request.IsEnabled,
            request.Note,
            Actor(http),
            cancellationToken));
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
}).RequirePermission(PermissionCodes.FirewallUpdate);

api.MapDelete("/firewall/rules/{id:guid}", async (
    Guid id,
    HttpContext http,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    try
    {
        await administration.DeleteFirewallRuleAsync(id, Actor(http), cancellationToken);
        return Results.NoContent();
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
}).RequirePermission(PermissionCodes.FirewallDelete);

api.MapGet("/domains", async (
    HttpContext http,
    AdminApplicationService administration,
    PermissionService permissions,
    CancellationToken cancellationToken) =>
{
    var scope = await permissions.GetPermittedDomainScopeAsync(http.User, PermissionCodes.DomainList, cancellationToken);
    return Results.Ok(await administration.GetDomainsAsync(scope, cancellationToken));
}).RequireAnyDomainPermission(PermissionCodes.DomainList);

api.MapGet("/domains/search", async (
    string? search,
    bool? isActive,
    bool? hasCertificate,
    OutboundMode? outboundMode,
    DomainExpiryFilter? expiry,
    int? page,
    int? pageSize,
    HttpContext http,
    AdminApplicationService administration,
    PermissionService permissions,
    CancellationToken cancellationToken) =>
{
    var scope = await permissions.GetPermittedDomainScopeAsync(http.User, PermissionCodes.DomainList, cancellationToken);
    return Results.Ok(await administration.SearchDomainsAsync(
        new DomainSearchFilter(search, isActive, hasCertificate, outboundMode, expiry ?? DomainExpiryFilter.Any),
        page ?? 1,
        pageSize ?? 20,
        scope,
        cancellationToken));
}).RequireAnyDomainPermission(PermissionCodes.DomainList);

api.MapGet("/domains/{id:guid}", async (
    Guid id,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await administration.GetDomainAsync(id, cancellationToken));
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
}).RequireDomainPermission(PermissionCodes.DomainDetail);

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
}).RequireAllDomainsPermission(PermissionCodes.DomainManage);

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
}).RequireDomainPermission(PermissionCodes.DomainUpdate);

api.MapDelete("/domains/{id:guid}", async (
    Guid id,
    HttpContext http,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    try
    {
        await administration.DeleteDomainsAsync([id], Actor(http), cancellationToken);
        return Results.NoContent();
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
}).RequireDomainPermission(PermissionCodes.DomainDelete);

api.MapPost("/domains/batch/outbound-mode", async (
    DomainBatchOutboundRequest request,
    HttpContext http,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    try
    {
        await administration.UpdateDomainsOutboundModeAsync(request.DomainIds, request.OutboundMode, Actor(http), cancellationToken);
        return Results.NoContent();
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
}).RequireBatchDomainPermission(PermissionCodes.DomainUpdate);

api.MapPost("/domains/batch/delete", async (
    DomainBatchRequest request,
    HttpContext http,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    try
    {
        await administration.DeleteDomainsAsync(request.DomainIds, Actor(http), cancellationToken);
        return Results.NoContent();
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
}).RequireBatchDomainPermission(PermissionCodes.DomainDelete);

api.MapPut("/domains/{id:guid}/details", async (
    Guid id,
    DomainDetailsRequest request,
    HttpContext http,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await administration.UpdateDomainDetailsAsync(
            id,
            request.MailHostName,
            request.OutboundMode,
            request.IsActive,
            request.SmtpHostName,
            request.OutboundIpAddresses,
            request.DirectDeliverSameDomain,
            Actor(http),
            cancellationToken));
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (Exception exception) when (exception is FormatException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequireDomainPermission(PermissionCodes.DomainUpdate);

api.MapPut("/domains/{id:guid}/quota", async (
    Guid id,
    DomainQuotaRequest request,
    HttpContext http,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await administration.UpdateDomainQuotaAsync(
            id,
            request.QuotaBytes,
            Actor(http),
            cancellationToken));
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (Exception exception) when (exception is ArgumentOutOfRangeException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequireDomainPermission(PermissionCodes.DomainQuotaUpdate);

api.MapPost("/domains/{id:guid}/certificate/inspect-upload", async (
    Guid id,
    HttpRequest request,
    DomainCertificateService certificates,
    CancellationToken cancellationToken) =>
{
    try
    {
        var form = await request.ReadFormAsync(cancellationToken);
        var certificate = RequireFile(form.Files, "certificate");
        var privateKey = RequireFile(form.Files, "privateKey");
        return Results.Ok(await certificates.InspectUploadAsync(
            id,
            await ReadLimitedAsync(certificate, DomainCertificateService.MaximumCertificateBytes, cancellationToken),
            await ReadLimitedAsync(privateKey, DomainCertificateService.MaximumPrivateKeyBytes, cancellationToken),
            form["password"].ToString(),
            cancellationToken));
    }
    catch (Exception exception) when (
        exception is IOException or UnauthorizedAccessException or InvalidOperationException or CryptographicException or KeyNotFoundException)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).DisableAntiforgery()
    .RequireDomainPermission(PermissionCodes.DomainCertificateUpdate);

api.MapPost("/domains/{id:guid}/certificate/upload", async (
    Guid id,
    HttpRequest request,
    HttpContext http,
    DomainCertificateService certificates,
    CancellationToken cancellationToken) =>
{
    try
    {
        var form = await request.ReadFormAsync(cancellationToken);
        var certificate = RequireFile(form.Files, "certificate");
        var privateKey = RequireFile(form.Files, "privateKey");
        return Results.Ok(await certificates.SaveUploadAsync(
            id,
            await ReadLimitedAsync(certificate, DomainCertificateService.MaximumCertificateBytes, cancellationToken),
            await ReadLimitedAsync(privateKey, DomainCertificateService.MaximumPrivateKeyBytes, cancellationToken),
            form["password"].ToString(),
            Actor(http),
            cancellationToken));
    }
    catch (Exception exception) when (
        exception is IOException or UnauthorizedAccessException or InvalidOperationException or CryptographicException or KeyNotFoundException)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).DisableAntiforgery()
    .RequireDomainPermission(PermissionCodes.DomainCertificateUpdate);

api.MapPost("/domains/{id:guid}/certificate/inspect-path", async (
    Guid id,
    CertificatePathRequest request,
    DomainCertificateService certificates,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await certificates.InspectExternalAsync(
            id,
            request.CertificatePath,
            request.PrivateKeyPath,
            cancellationToken));
    }
    catch (Exception exception) when (
        exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or CryptographicException or KeyNotFoundException)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequireDomainPermission(PermissionCodes.DomainCertificateUpdate);

api.MapPut("/domains/{id:guid}/certificate/path", async (
    Guid id,
    CertificatePathRequest request,
    HttpContext http,
    DomainCertificateService certificates,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await certificates.BindExternalAsync(
            id,
            request.CertificatePath,
            request.PrivateKeyPath,
            Actor(http),
            cancellationToken));
    }
    catch (Exception exception) when (
        exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or CryptographicException or KeyNotFoundException)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequireDomainPermission(PermissionCodes.DomainCertificateUpdate);

api.MapDelete("/domains/{id:guid}/certificate", async (
    Guid id,
    HttpContext http,
    DomainCertificateService certificates,
    CancellationToken cancellationToken) =>
{
    await certificates.DeleteAsync(id, Actor(http), cancellationToken);
    return Results.NoContent();
}).RequireDomainPermission(PermissionCodes.DomainCertificateDelete);

api.MapGet("/domains/{id:guid}/dns", async (
    Guid id,
    DomainDnsService dns,
    CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await dns.GetAsync(id, cancellationToken)); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
}).RequireDomainPermission(PermissionCodes.DomainDnsDetail);

api.MapPut("/domains/{id:guid}/dns", async (
    Guid id,
    DomainDnsSettings request,
    HttpContext http,
    DomainDnsService dns,
    CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await dns.SaveAsync(id, request, Actor(http), cancellationToken)); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (FormatException exception) { return Results.BadRequest(new { error = exception.Message }); }
}).RequireDomainPermission(PermissionCodes.DomainDnsUpdate);

api.MapPost("/domains/{id:guid}/dkim", async (
    Guid id,
    DkimGenerateRequest request,
    HttpContext http,
    DomainDnsService dns,
    CancellationToken cancellationToken) =>
{
    try
    {
        var publicKey = await dns.GenerateDkimAsync(id, request.Selector, Actor(http), cancellationToken);
        return Results.Ok(new { publicKey });
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (FormatException exception) { return Results.BadRequest(new { error = exception.Message }); }
}).RequireDomainPermission(PermissionCodes.DomainDnsUpdate);

api.MapPost("/domains/{id:guid}/dns/validate", async (
    Guid id,
    DomainDnsService dns,
    CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await dns.ValidatePublishedAsync(id, cancellationToken)); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
}).RequireDomainPermission(PermissionCodes.DomainDnsDetail);

api.MapGet("/domains/{id:guid}/dns/check", async (
    Guid id,
    DomainDnsService dns,
    CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await dns.GetCachedCheckAsync(id, cancellationToken)); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
}).RequireDomainPermission(PermissionCodes.DomainDnsDetail);

api.MapPost("/domains/{id:guid}/dns/check", async (
    Guid id,
    DomainDnsService dns,
    CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await dns.CheckAndStoreAsync(id, cancellationToken)); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
}).RequireDomainPermission(PermissionCodes.DomainDnsCheck);

api.MapPost("/domains/batch/dns-check", async (
    DomainBatchRequest request,
    DomainDnsService dns,
    CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await dns.CheckManyAsync(request.DomainIds, cancellationToken)); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
}).RequireBatchDomainPermission(PermissionCodes.DomainDnsCheck);

api.MapGet("/domains/{id:guid}/health", async (
    Guid id,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await administration.GetDomainHealthAsync(id, cancellationToken)); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
}).RequireDomainPermission(PermissionCodes.DomainDetail);

api.MapGet("/domains/{id:guid}/accounts", async (
    Guid id,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await administration.GetDomainAccountsAsync(id, cancellationToken)); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
}).RequireDomainPermission(PermissionCodes.DomainAccountList);

api.MapGet("/domains/{id:guid}/accounts/search", async (
    Guid id,
    string? query,
    bool? isEnabled,
    int? page,
    int? pageSize,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await administration.SearchDomainAccountsAsync(
            id,
            query,
            isEnabled,
            page ?? 1,
            pageSize ?? 10,
            cancellationToken));
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
}).RequireDomainPermission(PermissionCodes.DomainAccountList);

api.MapPost("/domains/{id:guid}/accounts", async (
    Guid id,
    DomainAccountRequest request,
    HttpContext http,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    try
    {
        var account = await administration.CreateDomainAccountAsync(
            id,
            request.LocalPart,
            request.Password,
            request.QuotaBytes,
            Actor(http),
            cancellationToken);
        return Results.Created($"/api/v1/domains/{id}/accounts/{account.Id}", account);
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (FormatException exception) { return Results.BadRequest(new { error = exception.Message }); }
    catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
}).RequireDomainPermission(PermissionCodes.DomainAccountCreate);

api.MapGet("/domains/{id:guid}/aliases", async (
    Guid id,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await administration.GetDomainAliasesAsync(id, cancellationToken)); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
}).RequireDomainPermission(PermissionCodes.DomainAliasList);

api.MapPost("/domains/{id:guid}/aliases", async (
    Guid id,
    DomainAliasRequest request,
    HttpContext http,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    try
    {
        var alias = await administration.CreateDomainAliasAsync(
            id,
            request.SourceLocalPart,
            request.TargetAccountId,
            request.Mode,
            Actor(http),
            cancellationToken);
        return Results.Created($"/api/v1/domains/{id}/aliases/{alias.Id}", alias);
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (Exception exception) when (exception is FormatException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequireDomainPermission(PermissionCodes.DomainAliasCreate);

api.MapPut("/domains/{id:guid}/aliases/{aliasId:guid}", async (
    Guid id,
    Guid aliasId,
    RebindAliasRequest request,
    HttpContext http,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    try
    {
        var aliases = await administration.GetDomainAliasesAsync(id, cancellationToken);
        if (aliases.All(x => x.Id != aliasId)) return Results.NotFound();
        return Results.Ok(await administration.UpdateAliasAsync(
            aliasId,
            request.SourceLocalPart,
            request.TargetAccountId,
            request.Mode,
            Actor(http),
            cancellationToken));
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (FormatException exception) { return Results.BadRequest(new { error = exception.Message }); }
    catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
}).RequireDomainPermission(PermissionCodes.DomainAliasUpdate);

api.MapGet("/domains/{id:guid}/rules", async (
    Guid id,
    MailRuleDirection? direction,
    MailRuleService ruleService,
    CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await ruleService.GetRulesAsync(id, direction ?? MailRuleDirection.Incoming, cancellationToken)); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
}).RequireDomainPermission(PermissionCodes.DomainRuleList);

api.MapGet("/domains/{id:guid}/rule-folders", async (
    Guid id,
    MailRuleService ruleService,
    CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await ruleService.GetFolderCoverageAsync(id, cancellationToken)); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
}).RequireDomainPermission(PermissionCodes.DomainRuleList);

api.MapPost("/domains/{id:guid}/rules", async (
    Guid id,
    MailRuleInput request,
    HttpContext http,
    MailRuleService ruleService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var rule = await ruleService.CreateAsync(id, request, Actor(http), cancellationToken);
        return Results.Created($"/api/v1/domains/{id}/rules/{rule.Id}", rule);
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (Exception exception) when (exception is FormatException or InvalidOperationException)
    { return Results.BadRequest(new { error = exception.Message }); }
}).RequireDomainPermission(PermissionCodes.DomainRuleCreate);

api.MapPut("/domains/{id:guid}/rules/{ruleId:guid}", async (
    Guid id,
    Guid ruleId,
    MailRuleInput request,
    HttpContext http,
    MailRuleService ruleService,
    CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await ruleService.UpdateAsync(id, ruleId, request, Actor(http), cancellationToken)); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (Exception exception) when (exception is FormatException or InvalidOperationException)
    { return Results.BadRequest(new { error = exception.Message }); }
}).RequireDomainPermission(PermissionCodes.DomainRuleUpdate);

api.MapDelete("/domains/{id:guid}/rules/{ruleId:guid}", async (
    Guid id,
    Guid ruleId,
    HttpContext http,
    MailRuleService ruleService,
    CancellationToken cancellationToken) =>
{
    try
    {
        await ruleService.DeleteAsync(id, ruleId, Actor(http), cancellationToken);
        return Results.NoContent();
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
}).RequireDomainPermission(PermissionCodes.DomainRuleDelete);

api.MapPut("/domains/{id:guid}/rules/order", async (
    Guid id,
    ReorderRulesRequest request,
    HttpContext http,
    MailRuleService ruleService,
    CancellationToken cancellationToken) =>
{
    try
    {
        await ruleService.ReorderAsync(id, request.Direction, request.RuleIds, Actor(http), cancellationToken);
        return Results.NoContent();
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
}).RequireDomainPermission(PermissionCodes.DomainRuleReorder);

api.MapGet("/domains/{id:guid}/audit", async (
    Guid id,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await administration.GetDomainAuditAsync(id, cancellationToken)); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
}).RequireDomainPermission(PermissionCodes.DomainAuditList);

api.MapGet("/accounts", async (
    HttpContext http,
    AdminApplicationService administration,
    PermissionService permissions,
    CancellationToken cancellationToken) =>
{
    var scope = await permissions.GetPermittedDomainScopeAsync(http.User, PermissionCodes.DomainAccountList, cancellationToken);
    return Results.Ok(await administration.GetAccountsAsync(scope, cancellationToken));
}).RequireAnyDomainPermission(PermissionCodes.DomainAccountList);

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
}).RequireAddressDomainPermission(PermissionCodes.DomainAccountCreate, nameof(AccountRequest.Address));

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
}).RequireAccountDomainPermission(PermissionCodes.DomainAccountPassword);

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
    catch (ArgumentOutOfRangeException exception) { return Results.BadRequest(new { error = exception.Message }); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
}).RequireAccountDomainPermission(PermissionCodes.DomainAccountUpdate);

api.MapGet("/aliases", async (
    HttpContext http,
    AdminApplicationService administration,
    PermissionService permissions,
    CancellationToken cancellationToken) =>
{
    var scope = await permissions.GetPermittedDomainScopeAsync(http.User, PermissionCodes.DomainAliasList, cancellationToken);
    return Results.Ok(await administration.GetAliasesAsync(scope, cancellationToken));
}).RequireAnyDomainPermission(PermissionCodes.DomainAliasList);

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
            request.TargetAccountId,
            Actor(http),
            cancellationToken);
        return Results.Created($"/api/v1/aliases/{alias.Id}", alias);
    }
    catch (Exception exception) when (exception is FormatException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequireAddressDomainPermission(PermissionCodes.DomainAliasCreate, nameof(AliasRequest.Source));

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
}).RequireAliasDomainPermission(PermissionCodes.DomainAliasDelete);

api.MapGet("/permissions/catalog", (PermissionService permissions) =>
    Results.Ok(permissions.GetCatalog()))
    .RequirePermission(PermissionCodes.IamManage);

api.MapGet("/admins", async (
    string? search,
    AdminUserStatusFilter? status,
    int? page,
    int? pageSize,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
    Results.Ok(await administration.SearchAdminUsersAsync(
        search ?? string.Empty,
        status ?? AdminUserStatusFilter.Any,
        Math.Max(1, page ?? 1),
        Math.Clamp(pageSize ?? 20, 1, 100),
        cancellationToken)))
    .RequirePermission(PermissionCodes.AdminAccountList);

api.MapPost("/admins", async (
    AdminUserRequest request,
    HttpContext http,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    try
    {
        var admin = await administration.CreateAdminUserAsync(
            request.UserName,
            request.Password,
            request.IsEnabled,
            request.Grants,
            Actor(http),
            cancellationToken);
        return Results.Created($"/api/v1/admins/{admin.Id}", admin);
    }
    catch (Exception exception) when (exception is FormatException or InvalidOperationException or KeyNotFoundException)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequirePermission(PermissionCodes.AdminAccountCreate)
    .RequirePermission(PermissionCodes.IamManage);

api.MapPatch("/admins/{id:guid}", async (
    Guid id,
    UpdateAdminUserRequest request,
    HttpContext http,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await administration.UpdateAdminUserAsync(
            id,
            request.UserName,
            request.IsEnabled,
            request.Password,
            Actor(http),
            cancellationToken));
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (Exception exception) when (exception is FormatException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequirePermission(PermissionCodes.AdminAccountUpdate);

api.MapDelete("/admins/{id:guid}", async (
    Guid id,
    HttpContext http,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    try
    {
        await administration.DeleteAdminUserAsync(id, Actor(http), cancellationToken);
        return Results.NoContent();
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
}).RequirePermission(PermissionCodes.AdminAccountDelete);

api.MapGet("/admins/{id:guid}/grants", async (
    Guid id,
    PermissionService permissions,
    CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await permissions.GetGrantsAsync(PermissionSubjectType.AdminUser, id, cancellationToken)); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
}).RequirePermission(PermissionCodes.IamManage);

api.MapPut("/admins/{id:guid}/grants", async (
    Guid id,
    PermissionGrantsRequest request,
    HttpContext http,
    PermissionService permissions,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await permissions.ReplaceGrantsAsync(
            PermissionSubjectType.AdminUser,
            id,
            request.Grants,
            Actor(http),
            cancellationToken));
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
}).RequirePermission(PermissionCodes.IamManage);

api.MapGet("/api-keys/{id:guid}/grants", async (
    Guid id,
    PermissionService permissions,
    CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await permissions.GetGrantsAsync(PermissionSubjectType.ApiKey, id, cancellationToken)); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
}).RequirePermission(PermissionCodes.IamManage);

api.MapPut("/api-keys/{id:guid}/grants", async (
    Guid id,
    PermissionGrantsRequest request,
    HttpContext http,
    PermissionService permissions,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await permissions.ReplaceGrantsAsync(
            PermissionSubjectType.ApiKey,
            id,
            request.Grants,
            Actor(http),
            cancellationToken));
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (InvalidOperationException exception) { return Results.BadRequest(new { error = exception.Message }); }
}).RequirePermission(PermissionCodes.IamManage);

api.MapPost("/api-keys", async (
    ApiKeyRequest request,
    HttpContext http,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    try
    {
        var key = await administration.CreateApiKeyAsync(
            request.Name,
            request.Grants,
            request.ExpiresAt,
            Actor(http),
            cancellationToken);
        return Results.Ok(new { key.Id, apiKey = key.Value, key.ExpiresAt });
    }
    catch (Exception exception) when (exception is FormatException or InvalidOperationException or KeyNotFoundException)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequirePermission(PermissionCodes.ApiKeyCreate)
    .RequirePermission(PermissionCodes.IamManage);

api.MapGet("/api-keys", async (
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
    Results.Ok(await administration.GetApiKeysAsync(cancellationToken)))
    .RequirePermission(PermissionCodes.ApiKeyList);

api.MapDelete("/api-keys/{id:guid}", async (
    Guid id,
    HttpContext http,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    try
    {
        await administration.DeleteApiKeyAsync(id, Actor(http), cancellationToken);
        return Results.NoContent();
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
}).RequirePermission(PermissionCodes.ApiKeyDelete);

api.MapPost("/api-keys/{id:guid}/refresh", async (
    Guid id,
    HttpContext http,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    try
    {
        var key = await administration.RefreshApiKeyAsync(id, Actor(http), cancellationToken);
        return Results.Ok(new { key.Id, apiKey = key.Value, key.ExpiresAt });
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
}).RequirePermission(PermissionCodes.ApiKeyRefresh);

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
    Results.Ok(await administration.GetDeliveryJobsAsync(status, cancellationToken)))
    .RequirePermission(PermissionCodes.QueueList);

api.MapGet("/queues/{id:guid}/raw", async (
    Guid id,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await administration.GetDeliveryRawMessageAsync(id, cancellationToken));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound(new { error = "Delivery job not found." });
    }
    catch (FileNotFoundException exception)
    {
        return Results.NotFound(new { error = exception.Message });
    }
}).RequirePermission(PermissionCodes.QueueList);

api.MapPost("/queues/{id:guid}/retry", async (
    Guid id,
    HttpContext http,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    await administration.RetryDeliveryAsync(id, Actor(http), cancellationToken);
    return Results.Accepted();
}).RequirePermission(PermissionCodes.QueueRetry);

api.MapDelete("/queues/{id:guid}", async (
    Guid id,
    HttpContext http,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    try
    {
        await administration.DeleteDeliveryAsync(id, Actor(http), cancellationToken);
        return Results.NoContent();
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
}).RequirePermission(PermissionCodes.QueueDelete);

api.MapGet("/domains/{domainId:guid}/queue", async (
    Guid domainId,
    string? search,
    DeliveryStatus? status,
    int? page,
    int? pageSize,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
    Results.Ok(await administration.SearchDeliveryJobsAsync(
        search,
        status,
        page ?? 1,
        pageSize ?? 10,
        cancellationToken,
        domainId)))
    .RequireDomainPermission(PermissionCodes.DomainRead, "domainId");

api.MapGet("/domains/{domainId:guid}/queue/{id:guid}/raw", async (
    Guid domainId,
    Guid id,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await administration.GetDeliveryRawMessageAsync(id, cancellationToken, domainId));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound(new { error = "Delivery job not found in this domain." });
    }
    catch (FileNotFoundException exception)
    {
        return Results.NotFound(new { error = exception.Message });
    }
}).RequireDomainPermission(PermissionCodes.DomainRead, "domainId");

api.MapPost("/domains/{domainId:guid}/queue/{id:guid}/retry", async (
    Guid domainId,
    Guid id,
    HttpContext http,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    try
    {
        await administration.RetryDeliveryAsync(id, Actor(http), cancellationToken, domainId);
        return Results.Accepted();
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
}).RequireDomainPermission(PermissionCodes.DomainManage, "domainId");

api.MapDelete("/domains/{domainId:guid}/queue/{id:guid}", async (
    Guid domainId,
    Guid id,
    HttpContext http,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    try
    {
        await administration.DeleteDeliveryAsync(id, Actor(http), cancellationToken, domainId);
        return Results.NoContent();
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
}).RequireDomainPermission(PermissionCodes.DomainManage, "domainId");

api.MapGet("/settings/delivery", async (
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
    Results.Ok(await administration.GetDeliverySettingsAsync(cancellationToken)))
    .RequirePermission(PermissionCodes.SettingsDetail);

api.MapGet("/settings/smtp", async (
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
    Results.Ok(await administration.GetSmtpSettingsAsync(cancellationToken)))
    .RequirePermission(PermissionCodes.SettingsDetail);

api.MapPut("/settings/smtp", async (
    SmtpSettingsView request,
    AdminApplicationService administration,
    HttpContext http,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await administration.UpdateSmtpSettingsAsync(
            request,
            Actor(http),
            cancellationToken));
    }
    catch (ArgumentOutOfRangeException exception) { return Results.BadRequest(new { error = exception.Message }); }
}).RequirePermission(PermissionCodes.SettingsUpdate);

api.MapGet("/plugins", async (
    PluginSettingsService plugins,
    CancellationToken cancellationToken) =>
    Results.Ok(await plugins.ListAsync(cancellationToken)))
    .RequirePermission(PermissionCodes.SettingsDetail);

api.MapPut("/plugins/order", async (
    PluginOrderRequest request,
    PluginSettingsService plugins,
    CancellationToken cancellationToken) =>
{
    try
    {
        await plugins.SaveOrderAsync(request.PluginIds, cancellationToken);
        return Results.Ok(await plugins.ListAsync(cancellationToken));
    }
    catch (FormatException exception) { return Results.BadRequest(new { error = exception.Message }); }
}).RequirePermission(PermissionCodes.SettingsUpdate);

api.MapGet("/plugins/{id}/configuration", async (
    string id,
    PluginSettingsService plugins,
    CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await plugins.GetConfigurationAsync(id, cancellationToken)); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
}).RequirePermission(PermissionCodes.SettingsDetail);

api.MapPut("/plugins/{id}/configuration", async (
    string id,
    PluginConfigurationRequest request,
    PluginSettingsService plugins,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await plugins.SaveAsync(
            id,
            request.Enabled,
            request.Values,
            request.ClearedSecrets.ToHashSet(StringComparer.OrdinalIgnoreCase),
            cancellationToken));
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (FormatException exception) { return Results.BadRequest(new { error = exception.Message }); }
}).RequirePermission(PermissionCodes.SettingsUpdate);

api.MapPut("/settings/delivery", async (
    DeliverySettingsRequest request,
    HttpContext http,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await administration.UpdateDeliverySettingsAsync(
            new DeliverySettingsView(
                request.BatchSize,
                request.LeaseSeconds,
                request.MaximumAttempts,
                request.PollIntervalSeconds),
            Actor(http),
            cancellationToken));
    }
    catch (ArgumentOutOfRangeException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequirePermission(PermissionCodes.SettingsUpdate);

api.MapGet("/settings/mailboxes", async (
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
    Results.Ok(await administration.GetMailboxSettingsAsync(cancellationToken)))
    .RequirePermission(PermissionCodes.SettingsDetail);

api.MapPut("/settings/mailboxes", async (
    MailboxSettingsRequest request,
    HttpContext http,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await administration.UpdateMailboxSettingsAsync(
            new MailboxSettingsView(request.DefaultFolders),
            Actor(http),
            cancellationToken));
    }
    catch (FormatException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequirePermission(PermissionCodes.SettingsUpdate);

api.MapGet("/settings/message-templates", async (
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
    Results.Ok(await administration.GetMessageTemplateSettingsAsync(cancellationToken)))
    .RequirePermission(PermissionCodes.SettingsDetail);

api.MapPut("/settings/message-templates", async (
    MessageTemplateSettingsRequest request,
    HttpContext http,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await administration.UpdateMessageTemplateSettingsAsync(
            new MessageTemplateSettingsView(
                request.SenderDisplayName,
                request.PermanentFailureSubject,
                request.PermanentFailureBody,
                request.RetryExhaustedSubject,
                request.RetryExhaustedBody),
            Actor(http),
            cancellationToken));
    }
    catch (ArgumentOutOfRangeException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequirePermission(PermissionCodes.SettingsUpdate);

api.MapGet("/settings/certificates", async (
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
    Results.Ok(await administration.GetCertificateSelectionSettingsAsync(cancellationToken)))
    .RequirePermission(PermissionCodes.SettingsDetail);

api.MapPut("/settings/certificates", async (
    CertificateSelectionSettingsRequest request,
    HttpContext http,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await administration.UpdateCertificateSelectionSettingsAsync(
            new CertificateSelectionSettingsView(
                request.DefaultMailHostName,
                request.AllowSingleCertificateFallback,
                [],
                null),
            Actor(http),
            cancellationToken));
    }
    catch (FormatException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
}).RequirePermission(PermissionCodes.SettingsUpdate);

api.MapGet("/diagnostics/dns", async (
    string domain,
    string? selector,
    AdminApplicationService administration,
    CancellationToken cancellationToken) =>
    Results.Ok(await administration.DiagnoseDnsAsync(domain, selector, cancellationToken)))
    .RequirePermission(PermissionCodes.DnsDiagnose);

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
    var admin = new AdminUser { UserName = userName, PasswordHash = credentials.HashPassword(password) };
    db.AdminUsers.Add(admin);
    db.PermissionGrants.Add(new PermissionGrant
    {
        SubjectType = PermissionSubjectType.AdminUser,
        SubjectId = admin.Id,
        PermissionCode = PermissionCodes.FullAccess,
        CreatedBy = "bootstrap"
    });
    await db.SaveChangesAsync(cancellationToken);
    logger.LogWarning("Created bootstrap administrator {UserName}; rotate its password and configuration secret.", userName);
}

static string Actor(HttpContext http) => http.User.Identity?.Name ?? "unknown";

static bool IsStrongPassword(string password) => !string.IsNullOrWhiteSpace(password) && password.Length >= 12;

static IFormFile RequireFile(IFormFileCollection files, string name) =>
    files.GetFile(name) ?? throw new InvalidDataException($"Form file '{name}' is required.");

static async Task<byte[]> ReadLimitedAsync(
    IFormFile file,
    int maximumBytes,
    CancellationToken cancellationToken)
{
    if (file.Length <= 0 || file.Length > maximumBytes)
        throw new InvalidDataException($"File '{file.Name}' exceeds its allowed size.");
    await using var input = file.OpenReadStream();
    await using var output = new MemoryStream((int)file.Length);
    await input.CopyToAsync(output, cancellationToken);
    return output.ToArray();
}

public sealed record LoginRequest(string UserName, string Password);
public sealed record FirewallRuleRequest(string Cidr, string? Note);
public sealed record UpdateFirewallRuleRequest(bool IsEnabled, string? Note);
public sealed record DomainRequest(string Name, OutboundMode OutboundMode, string? DkimSelector, string? DkimPrivateKeyPath);
public sealed record UpdateDomainRequest(bool? IsActive, OutboundMode? OutboundMode, string? DkimSelector, string? DkimPrivateKeyPath);
public sealed record DomainDetailsRequest(
    string MailHostName,
    OutboundMode OutboundMode,
    bool IsActive,
    string? SmtpHostName,
    string? OutboundIpAddresses,
    bool? DirectDeliverSameDomain);
public sealed record DomainQuotaRequest(long QuotaBytes);
public sealed record CertificatePathRequest(string CertificatePath, string PrivateKeyPath);
public sealed record DkimGenerateRequest(string? Selector);
public sealed record AccountRequest(string Address, string Password, long? QuotaBytes);
public sealed record DomainAccountRequest(string LocalPart, string Password, long? QuotaBytes);
public sealed record PasswordRequest(string Password);
public sealed record UpdateAccountRequest(bool? IsEnabled, long? QuotaBytes);
public sealed record AliasRequest(string Source, Guid TargetAccountId);
public sealed record DomainAliasRequest(
    string SourceLocalPart,
    Guid TargetAccountId,
    MailAliasMode Mode = MailAliasMode.SendReceive);
public sealed record RebindAliasRequest(
    Guid? TargetAccountId,
    string? SourceLocalPart = null,
    MailAliasMode? Mode = null);
public sealed record DomainBatchRequest(Guid[] DomainIds);
public sealed record DomainBatchOutboundRequest(Guid[] DomainIds, OutboundMode OutboundMode);
public sealed record ReorderRulesRequest(MailRuleDirection Direction, Guid[] RuleIds);
public sealed record ApiKeyRequest(string Name, PermissionGrantInput[] Grants, DateTimeOffset? ExpiresAt);
public sealed record PermissionGrantsRequest(PermissionGrantInput[] Grants);
public sealed record AdminUserRequest(string UserName, string Password, bool IsEnabled, PermissionGrantInput[] Grants);
public sealed record UpdateAdminUserRequest(string UserName, bool IsEnabled, string? Password);
public sealed record ChangeAdminPasswordRequest(string CurrentPassword, string NewPassword);
public sealed record DeliverySettingsRequest(
    int BatchSize,
    int LeaseSeconds,
    int MaximumAttempts,
    int PollIntervalSeconds);
public sealed record MailboxSettingsRequest(string[] DefaultFolders);
public sealed record MessageTemplateSettingsRequest(
    string SenderDisplayName,
    string PermanentFailureSubject,
    string PermanentFailureBody,
    string RetryExhaustedSubject,
    string RetryExhaustedBody);
public sealed record CertificateSelectionSettingsRequest(
    string? DefaultMailHostName,
    bool AllowSingleCertificateFallback);
public sealed record PluginConfigurationRequest(
    bool Enabled,
    Dictionary<string, JsonElement> Values,
    string[] ClearedSecrets);
public sealed record PluginOrderRequest(string[] PluginIds);

public partial class Program;
