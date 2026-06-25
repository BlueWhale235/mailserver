using System.Security.Claims;
using System.Text.Encodings.Web;
using MailServer.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MailServer.Api;

public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IDbContextFactory<MailDbContext> dbFactory,
    MailServer.Domain.ICredentialService credentials)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Api-Key", out var header))
        {
            return AuthenticateResult.NoResult();
        }

        var value = header.ToString();
        var parts = value.Split('_', 3);
        if (parts.Length != 3 || parts[0] != "msk")
        {
            return AuthenticateResult.Fail("Invalid API key format.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(Context.RequestAborted);
        var record = await db.ApiKeys.SingleOrDefaultAsync(x => x.Prefix == parts[1], Context.RequestAborted);
        if (record is null
            || record.RevokedAt is not null
            || record.ExpiresAt <= DateTimeOffset.UtcNow
            || !credentials.VerifyApiSecret(record.SecretHash, parts[2]))
        {
            return AuthenticateResult.Fail("Invalid API key.");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, record.Id.ToString()),
            new(ClaimTypes.Name, record.Name),
            new("auth_type", "api_key")
        };
        claims.AddRange(record.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(scope => new Claim("scope", scope)));
        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName));
    }
}
