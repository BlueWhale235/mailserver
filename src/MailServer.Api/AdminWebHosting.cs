using System.Net;
using System.Security.Claims;
using MailServer.Api.Components;
using MailServer.Infrastructure;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace MailServer.Api;

public static class AdminWebHosting
{
    public const string CookieScheme = "AdminCookie";

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
            if (options.Http.Enabled)
            {
                kestrel.Listen(address, options.Http.Port);
            }

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

    public static IServiceCollection AddMailServerAdminWeb(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var options = configuration.GetSection(AdminServerOptions.SectionName).Get<AdminServerOptions>()
            ?? new AdminServerOptions();
        services.AddRazorComponents().AddInteractiveServerComponents();
        services.AddCascadingAuthenticationState();
        services.AddAntiforgery();
        services.AddHttpContextAccessor();
        services.AddScoped<AdminToastService>();
        services.AddHttpsRedirection(redirect =>
        {
            redirect.HttpsPort = options.Https.Port;
            redirect.RedirectStatusCode = StatusCodes.Status308PermanentRedirect;
        });
        return services;
    }

    public static WebApplication MapMailServerAdminWeb(
        this WebApplication app,
        LoadedPemCertificate? adminCertificate)
    {
        var server = app.Configuration.GetSection(AdminServerOptions.SectionName).Get<AdminServerOptions>()
            ?? new AdminServerOptions();
        if (server.Http.Enabled && server.Https.Enabled && server.Https.RedirectHttp)
        {
            app.UseHttpsRedirection();
        }

        app.UseAntiforgery();
        app.MapPost("/admin/session/login", async (
            HttpContext context,
            AdminApplicationService administration,
            IAntiforgery antiforgery) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var admin = await administration.AuthenticateAsync(
                form["userName"].ToString(),
                form["password"].ToString(),
                context.RequestAborted);
            if (admin is null)
            {
                return Results.Redirect("/admin/login?error=1");
            }

            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, admin.Id.ToString()),
                new Claim(ClaimTypes.Name, admin.UserName),
                new Claim("auth_type", "admin")
            };
            await context.SignInAsync(
                CookieScheme,
                new ClaimsPrincipal(new ClaimsIdentity(claims, CookieScheme)),
                new AuthenticationProperties
                {
                    IsPersistent = form["remember"] == "on",
                    AllowRefresh = true
                });
            return Results.Redirect("/admin");
        }).AllowAnonymous();

        app.MapPost("/admin/logout", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            await context.SignOutAsync(CookieScheme);
            return Results.Redirect("/admin/login");
        }).RequireAuthorization();

        app.MapGet("/admin/delivery-queue/{id:guid}/download", async (
            Guid id,
            Guid? routeDomainId,
            HttpContext context,
            AdminApplicationService administration,
            PermissionService permissions) =>
        {
            var snapshot = await permissions.GetSnapshotAsync(context.User, context.RequestAborted);
            var allowed = routeDomainId is { } domainId
                ? snapshot.Has(PermissionCodes.DomainRead, domainId)
                : snapshot.Has(PermissionCodes.QueueRead);
            if (!allowed)
            {
                return Results.Forbid();
            }

            try
            {
                var download = await administration.OpenDeliveryMessageDownloadAsync(
                    id,
                    context.RequestAborted,
                    routeDomainId);
                return Results.Stream(download.Content, "message/rfc822", download.FileName);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (FileNotFoundException exception)
            {
                return Results.NotFound(new { error = exception.Message });
            }
        }).RequireAuthorization();

        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        app.Lifetime.ApplicationStopped.Register(() => adminCertificate?.Dispose());
        return app;
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
        });
}
