using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using MailServer.Infrastructure;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;

namespace MailServer.Api;

public static class AdminWebHosting
{
    public const string CookieScheme = "AdminCookie";
    public const string AntiforgeryHeader = "X-CSRF-TOKEN";

    public static LoadedPemCertificate? ConfigureAdminListeners(WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection(AdminServerOptions.SectionName).Get<AdminServerOptions>()
            ?? new AdminServerOptions();
        if (!options.Http.Enabled && !options.Https.Enabled)
            throw new InvalidOperationException("At least one AdminServer HTTP or HTTPS endpoint must be enabled.");
        if (!IPAddress.TryParse(options.BindAddress, out var address))
            throw new InvalidOperationException($"Invalid AdminServer bind address: {options.BindAddress}");

        var certificates = builder.Configuration.GetSection(CertificatesOptions.SectionName).Get<CertificatesOptions>()
            ?? new CertificatesOptions();
        var adminCertificate = options.Https.Enabled
            ? PemCertificateLoader.Load(certificates.Admin, "Admin")
            : null;
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            if (options.Http.Enabled) kestrel.Listen(address, options.Http.Port);
            if (options.Https.Enabled)
            {
                kestrel.Listen(address, options.Https.Port, listen =>
                    listen.UseHttps(https =>
                    {
                        https.ServerCertificate = adminCertificate!.Certificate;
                        https.ServerCertificateChain = adminCertificate.Intermediates;
                    }));
            }
        });
        return adminCertificate;
    }

    public static IServiceCollection AddMailServerAdminWeb(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(AdminServerOptions.SectionName).Get<AdminServerOptions>()
            ?? new AdminServerOptions();
        services.AddAntiforgery(value =>
        {
            value.HeaderName = AntiforgeryHeader;
            value.Cookie.Name = "MailServer.Csrf";
            value.Cookie.HttpOnly = true;
            value.Cookie.SameSite = SameSiteMode.Strict;
            value.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });
        services.AddRateLimiter(value => value.AddFixedWindowLimiter("admin-login", limiter =>
        {
            limiter.PermitLimit = 5;
            limiter.Window = TimeSpan.FromMinutes(1);
            limiter.QueueLimit = 0;
            limiter.AutoReplenishment = true;
        }));
        services.AddHttpsRedirection(redirect =>
        {
            redirect.HttpsPort = options.Https.Port;
            redirect.RedirectStatusCode = StatusCodes.Status308PermanentRedirect;
        });
        return services;
    }

    public static WebApplication MapMailServerAdminWeb(this WebApplication app, LoadedPemCertificate? adminCertificate)
    {
        var server = app.Configuration.GetSection(AdminServerOptions.SectionName).Get<AdminServerOptions>()
            ?? new AdminServerOptions();
        if (server.Http.Enabled && server.Https.Enabled && server.Https.RedirectHttp) app.UseHttpsRedirection();

        app.MapGet("/api/v1/session/csrf", (IAntiforgery antiforgery, HttpContext context) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            return Results.Ok(new { token = tokens.RequestToken, headerName = AntiforgeryHeader });
        }).AllowAnonymous();

        app.MapPost("/api/v1/session/login", async (
            SessionLoginRequest request,
            HttpContext context,
            AdminApplicationService administration,
            IAntiforgery antiforgery) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            var admin = await administration.AuthenticateAsync(request.UserName, request.Password, context.RequestAborted);
            if (admin is null) return AdminApiProblems.InvalidCredentials(context);

            var startedAt = DateTimeOffset.UtcNow;
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, admin.Id.ToString()),
                new Claim(ClaimTypes.Name, admin.UserName),
                new Claim("auth_type", "admin"),
                new Claim("session_started", startedAt.ToUnixTimeSeconds().ToString())
            };
            await context.SignInAsync(
                CookieScheme,
                new ClaimsPrincipal(new ClaimsIdentity(claims, CookieScheme)),
                new AuthenticationProperties
                {
                    IsPersistent = request.Remember,
                    AllowRefresh = true,
                    IssuedUtc = startedAt,
                    ExpiresUtc = startedAt.AddHours(8)
                });
            return Results.Ok(new { id = admin.Id, userName = admin.UserName, expiresAt = startedAt.AddHours(8) });
        }).AllowAnonymous().RequireRateLimiting("admin-login");

        app.MapGet("/api/v1/session/me", (HttpContext context) => Results.Ok(new
        {
            id = context.User.FindFirstValue(ClaimTypes.NameIdentifier),
            userName = context.User.Identity?.Name,
            authenticated = context.User.Identity?.IsAuthenticated == true
        })).RequireAuthorization("admin");

        app.MapGet("/api/v1/session/permissions", async (
            HttpContext context,
            PermissionService permissions,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await permissions.GetSnapshotAsync(context.User, cancellationToken);
            return Results.Ok(new
            {
                fullAccess = snapshot.FullAccess,
                systemPermissions = snapshot.SystemPermissions.OrderBy(code => code, StringComparer.OrdinalIgnoreCase),
                allDomainPermissions = snapshot.AllDomainPermissions.OrderBy(code => code, StringComparer.OrdinalIgnoreCase),
                domainPermissions = snapshot.DomainPermissions.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.OrderBy(id => id).Select(id => id.ToString()))
            });
        }).RequireAuthorization("admin");

        app.MapPost("/api/v1/session/logout", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            await context.SignOutAsync(CookieScheme);
            return Results.NoContent();
        }).RequireAuthorization("admin");

        MapAdminSpaFallback(app);
        app.MapGet("/", () => Results.Redirect("/admin"));
        app.Lifetime.ApplicationStopped.Register(() => adminCertificate?.Dispose());
        return app;
    }

    internal static void MapAdminSpaFallback(WebApplication app)
    {
        // Existing files are served by StaticFileMiddleware. A missing Vite asset
        // must remain 404 instead of falling through to the SPA document.
        app.MapGet("/admin/assets/{**path}", () => Results.NotFound()).AllowAnonymous();

        // Do not use the :nonfile constraint here: domain routes can legitimately
        // end with values such as example.com and still belong to the Vue router.
        app.MapGet("/admin/{**path}", (IWebHostEnvironment environment) =>
        {
            var indexPath = Path.Combine(environment.WebRootPath, "admin", "index.html");
            return File.Exists(indexPath)
                ? Results.File(indexPath, "text/html; charset=utf-8")
                : Results.Problem("The Vue administration bundle is not available.", statusCode: 503);
        }).AllowAnonymous();
    }

    public static AuthenticationBuilder AddAdminCookie(this AuthenticationBuilder authentication) =>
        authentication.AddCookie(CookieScheme, options =>
        {
            options.LoginPath = "/admin/login";
            options.AccessDeniedPath = "/admin/login";
            options.Cookie.Name = "MailServer.Admin";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
            options.SlidingExpiration = true;
            options.Events.OnRedirectToLogin = context => HandleCookieRedirect(context, StatusCodes.Status401Unauthorized);
            options.Events.OnRedirectToAccessDenied = context => HandleCookieRedirect(context, StatusCodes.Status403Forbidden);
        });

    private static Task HandleCookieRedirect(RedirectContext<CookieAuthenticationOptions> context, int statusCode)
    {
        if (context.Request.Path.StartsWithSegments("/api")) context.Response.StatusCode = statusCode;
        else context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    }
}

public sealed record SessionLoginRequest(string UserName, string Password, bool Remember = false);
