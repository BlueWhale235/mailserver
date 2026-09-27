using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using MailServer.Api;
using MailServer.Domain;
using MailServer.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MailServer.Tests;

// Real Kestrel subprocess, real SQLite migrations and HTTP authentication.
// No in-memory database or mocked endpoint handlers are used by these tests.
public sealed class HttpAcceptanceTests
{
    [Fact]
    public async Task ResourceProfilesPropagateFromIndependentProcessToRunAll()
    {
        await using var host = await AcceptanceHost.StartAsync(runAll: true);
        foreach (var profileId in new[] { "4gb", "512mb" })
        {
            // This service writes from the test process; the mail host has its own cache and monitor.
            await host.SaveResourceProfileAsync(profileId);
            var applied = false;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var status = await host.JsonAsync(HttpMethod.Get, "/api/v1/settings/resources/status");
                applied = status.GetProperty("processes").EnumerateArray().Any(x =>
                    x.GetProperty("state").GetString() == "applied"
                    && x.GetProperty("usage").GetProperty("profileId").GetString() == profileId);
                if (applied)
                {
                    Assert.Equal(ResourceProfiles.Get(profileId).CacheBytes,
                        status.GetProperty("totalCacheBudgetBytes").GetInt64());
                    break;
                }
                await Task.Delay(100);
            }
            Assert.True(applied, $"Independent mail host did not apply {profileId}.");
        }
    }

    [Fact]
    public async Task ResourceSettingsAreValidatedPersistedAndPermissionProtected()
    {
        await using var host = await AcceptanceHost.StartAsync();
        const string path = "/api/v1/settings/resources";
        var original = await host.JsonAsync(HttpMethod.Get, path);
        Assert.Equal(6, original.GetProperty("profiles").GetArrayLength());
        await host.JsonAsync(HttpMethod.Put, path, new { profileId = "invalid" }, 400);
        await host.JsonAsync(HttpMethod.Put, path, new { profileId = "4gb" });
        Assert.Equal("4gb", (await host.JsonAsync(HttpMethod.Get, path)).GetProperty("profileId").GetString());
        var status = await host.JsonAsync(HttpMethod.Get, path + "/status");
        Assert.Empty(status.GetProperty("processes").EnumerateArray());
        using var anonymous = await host.Anonymous.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        await host.JsonAsync(HttpMethod.Post, "/api/v1/admins", new { userName = "resources-reader", password = AcceptanceHost.Password,
            isEnabled = true, grants = new[] { new { permissionCode = "settings.read", resourceType = "", resourceKey = "" } } }, 201);
        using var reader = await host.LoginAsync("resources-reader");
        using var read = await reader.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var forbidden = await reader.PutAsJsonAsync(path, new { profileId = "16gb" });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        await using var db = await host.Factory.CreateDbContextAsync();
        Assert.Equal("4gb", (await db.SystemSettings.SingleAsync(x => x.Key == ResourceSettingsService.Key)).Value);
        Assert.True(await db.AuditRecords.AnyAsync(x => x.Action == "settings.resources.update"));
    }

    [Fact]
    public async Task QuotaTemplateEndpointsPreserveLegacyUpdatesAndPreviewQuotaReason()
    {
        await using var host = await AcceptanceHost.StartAsync();
        const string path = "/api/v1/settings/message-templates";
        var defaults = await host.JsonAsync(HttpMethod.Get, path + "/defaults");
        Assert.Contains("手动联系收件人", defaults.GetProperty("quotaExceededBody").GetString());
        var settings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(defaults.GetRawText())!;
        settings["quotaExceededSubject"] = JsonSerializer.SerializeToElement("自定义超限退信");
        var saved = await host.JsonAsync(HttpMethod.Put, path, settings);
        Assert.Equal("自定义超限退信", saved.GetProperty("quotaExceededSubject").GetString());
        settings.Remove("quotaExceededSubject");
        settings.Remove("quotaExceededBody");
        settings["senderDisplayName"] = JsonSerializer.SerializeToElement("Legacy client");
        var legacy = await host.JsonAsync(HttpMethod.Put, path, settings);
        Assert.Equal("自定义超限退信", legacy.GetProperty("quotaExceededSubject").GetString());
        Assert.Equal(defaults.GetProperty("quotaExceededBody").GetString(), legacy.GetProperty("quotaExceededBody").GetString());
        var reloaded = await host.JsonAsync(HttpMethod.Get, path);
        Assert.Equal("自定义超限退信", reloaded.GetProperty("quotaExceededSubject").GetString());
        var preview = await host.JsonAsync(HttpMethod.Post, path + "/preview",
            new { subject = "超限：{recipient}", body = "<p>{reason}</p>", templateKind = "quota" });
        Assert.Contains("552 5.2.2", preview.GetProperty("renderedHtml").GetString());
        Assert.Contains("手动联系收件人", preview.GetProperty("renderedHtml").GetString());
        var oldPreview = await host.JsonAsync(HttpMethod.Post, path + "/preview", new { subject = "Failure", body = "<p>{reason}</p>" });
        Assert.Contains("550 5.1.1", oldPreview.GetProperty("renderedHtml").GetString());
    }

    [Fact]
    public async Task DomainDnsSettingsCanBeSavedFromGetResponse()
    {
        await using var host = await AcceptanceHost.StartAsync();
        var domain = await host.JsonAsync(HttpMethod.Post, "/api/v1/domains", new { name = "dns-save.invalid" }, 201);
        var domainId = domain.GetProperty("id").GetGuid();
        var path = $"/api/v1/domains/{domainId}/dns";
        var configuration = await host.JsonAsync(HttpMethod.Get, path);
        var settings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            configuration.GetProperty("settings").GetRawText())!;
        settings["spfIpv4"] = JsonSerializer.SerializeToElement("203.0.113.25");

        var saved = await host.JsonAsync(HttpMethod.Put, path, settings);
        Assert.Equal("203.0.113.25", saved.GetProperty("settings").GetProperty("spfIpv4").GetString());
        Assert.Contains("ip4:203.0.113.25", saved.GetProperty("records")[0].GetProperty("value").GetString());

        await using var db = await host.Factory.CreateDbContextAsync();
        Assert.Equal("203.0.113.25", (await db.DomainDnsSettings.SingleAsync(x => x.DomainId == domainId)).SpfIpv4);
        Assert.True(await db.AuditRecords.AnyAsync(x => x.Action == "domain.dns.update" && x.Actor == "acceptance-admin"));

        settings["spfIpv4"] = JsonSerializer.SerializeToElement<string?>(null);
        using var invalid = await host.Admin.PutAsJsonAsync(path, settings);
        var problem = await AcceptanceHost.AssertProblemAsync(invalid, 400);
        Assert.True(problem.GetProperty("errors").TryGetProperty("spfIpv4", out _));
        Assert.False(problem.GetProperty("errors").TryGetProperty("domain", out _));
    }

    [Fact]
    public async Task DomainDnsOptionalFieldsAndConditionalRequirementsWorkOverHttp()
    {
        await using var host = await AcceptanceHost.StartAsync();
        var domain = await host.JsonAsync(HttpMethod.Post, "/api/v1/domains", new { name = "dns-optional.invalid" }, 201);
        var domainId = domain.GetProperty("id").GetGuid();
        var path = $"/api/v1/domains/{domainId}/dns";
        var configuration = await host.JsonAsync(HttpMethod.Get, path);
        var settings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            configuration.GetProperty("settings").GetRawText())!;
        foreach (var field in new[]
        {
            "spfIpv4", "spfIpv6", "spfIncludes", "spfCustomMechanisms",
            "dmarcAggregateReportUris", "dmarcForensicReportUris", "dmarcFailureOptions",
            "mtaStsPolicyId", "mtaStsMxPatterns", "tlsReportUris", "daneTlsaRecords"
        }) settings[field] = JsonSerializer.SerializeToElement("");
        settings["dmarcPolicy"] = JsonSerializer.SerializeToElement("quarantine");
        settings["mtaStsMode"] = JsonSerializer.SerializeToElement("none");
        settings["mtaStsMaxAgeSeconds"] = JsonSerializer.SerializeToElement(-1);

        var saved = await host.JsonAsync(HttpMethod.Put, path, settings);
        Assert.Equal("0", saved.GetProperty("settings").GetProperty("dmarcFailureOptions").GetString());
        Assert.Equal("1", saved.GetProperty("settings").GetProperty("mtaStsPolicyId").GetString());
        Assert.Equal(604_800, saved.GetProperty("settings").GetProperty("mtaStsMaxAgeSeconds").GetInt32());
        Assert.Contains("fo=0", saved.GetProperty("records")[1].GetProperty("value").GetString());

        settings["tlsReportUris"] = JsonSerializer.SerializeToElement("mailto:reports@dns-optional.invalid");
        saved = await host.JsonAsync(HttpMethod.Put, path, settings);
        Assert.Contains(saved.GetProperty("records").EnumerateArray(), item =>
            item.GetProperty("name").GetString() == "_smtp._tls.dns-optional.invalid");

        settings["mtaStsMode"] = JsonSerializer.SerializeToElement("testing");
        settings["mtaStsMaxAgeSeconds"] = JsonSerializer.SerializeToElement(604_800);
        using (var missingMx = await host.Admin.PutAsJsonAsync(path, settings))
            Assert.Equal(HttpStatusCode.BadRequest, missingMx.StatusCode);
        settings["mtaStsPolicyId"] = JsonSerializer.SerializeToElement("20260925");
        settings["mtaStsMxPatterns"] = JsonSerializer.SerializeToElement("mail.dns-optional.invalid");
        saved = await host.JsonAsync(HttpMethod.Put, path, settings);
        Assert.Equal("testing", saved.GetProperty("settings").GetProperty("mtaStsMode").GetString());

        settings["daneEnabled"] = JsonSerializer.SerializeToElement(true);
        using (var missingTlsa = await host.Admin.PutAsJsonAsync(path, settings))
            Assert.Equal(HttpStatusCode.BadRequest, missingTlsa.StatusCode);
        settings["daneEnabled"] = JsonSerializer.SerializeToElement(false);
        await host.JsonAsync(HttpMethod.Put, path, settings);

        await using var db = await host.Factory.CreateDbContextAsync();
        Assert.True(await db.AuditRecords.CountAsync(x => x.Action == "domain.dns.update" && x.Actor == "acceptance-admin") >= 4);
    }

    [Fact]
    public async Task EveryOpenApiProtectedOperationRejectsAnonymousRequests()
    {
        await using var host = await AcceptanceHost.StartAsync();
        var specification = await host.JsonAsync(HttpMethod.Get, "/swagger/v1/swagger.json");
        var checkedOperations = 0;
        foreach (var path in specification.GetProperty("paths").EnumerateObject())
        {
            if (path.Name is "/api/v1/auth/token" or "/api/v1/session/csrf" or "/api/v1/session/login"
                || path.Name.StartsWith("/api/v1/health", StringComparison.Ordinal)) continue;
            foreach (var operation in path.Value.EnumerateObject())
            {
                if (operation.Name is not ("get" or "post" or "put" or "patch" or "delete")) continue;
                var url = Regex.Replace(path.Name, "\\{[^}]+\\}", _ => Guid.NewGuid().ToString());
                using var request = new HttpRequestMessage(new HttpMethod(operation.Name), url);
                if (operation.Name is "post" or "put" or "patch") request.Content = JsonContent.Create(new { });
                using var response = await host.Anonymous.SendAsync(request);
                Assert.True(response.StatusCode == HttpStatusCode.Unauthorized,
                    $"{operation.Name} {path.Name}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
                await AcceptanceHost.AssertProblemAsync(response, 401);
                checkedOperations++;
            }
        }
        Assert.True(checkedOperations >= 133, $"Only discovered {checkedOperations} protected operations.");
    }

    [Fact]
    public async Task AuthorizedReadRoutesAvoidServerErrors()
    {
        await using var host = await AcceptanceHost.StartAsync();
        var specification = await host.JsonAsync(HttpMethod.Get, "/swagger/v1/swagger.json");
        var failures = new List<string>();
        var checkedRoutes = 0;
        foreach (var path in specification.GetProperty("paths").EnumerateObject())
        {
            if (!path.Value.TryGetProperty("get", out _) || path.Name.StartsWith("/api/v1/session", StringComparison.Ordinal))
                continue;
            var url = Regex.Replace(path.Name, "\\{[^}]+\\}", _ => Guid.NewGuid().ToString());
            using var response = await host.Admin.GetAsync(url);
            if ((int)response.StatusCode >= 500)
                failures.Add($"GET {path.Name}: {(int)response.StatusCode}");
            checkedRoutes++;
        }
        Assert.True(checkedRoutes >= 55, $"Only checked {checkedRoutes} GET routes.");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task EmptyJsonBodiesNeverCauseInternalServerErrors()
    {
        await using var host = await AcceptanceHost.StartAsync();
        using (var emptyLogin = await host.Anonymous.PostAsJsonAsync("/api/v1/auth/token", new { }))
        {
            var problem = await AcceptanceHost.AssertProblemAsync(emptyLogin, 400);
            Assert.True(problem.GetProperty("errors").TryGetProperty("userName", out _));
            Assert.True(problem.GetProperty("errors").TryGetProperty("password", out _));
        }
        using (var emptySettings = await host.Admin.PutAsJsonAsync("/api/v1/settings/mailboxes", new { }))
        {
            var problem = await AcceptanceHost.AssertProblemAsync(emptySettings, 400);
            Assert.True(problem.GetProperty("errors").TryGetProperty("defaultFolders", out _));
        }
        var specification = await host.JsonAsync(HttpMethod.Get, "/swagger/v1/swagger.json");
        var failures = new List<string>();
        foreach (var path in specification.GetProperty("paths").EnumerateObject())
        {
            if (path.Name.StartsWith("/api/v1/session", StringComparison.Ordinal)) continue;
            foreach (var operation in path.Value.EnumerateObject())
            {
                if (operation.Name is not ("post" or "put" or "patch")
                    || !operation.Value.TryGetProperty("requestBody", out var body)
                    || !body.GetProperty("content").TryGetProperty("application/json", out _)) continue;
                var url = Regex.Replace(path.Name, "\\{[^}]+\\}", _ => Guid.NewGuid().ToString());
                using var request = new HttpRequestMessage(new HttpMethod(operation.Name), url) { Content = JsonContent.Create(new { }) };
                using var response = await host.Admin.SendAsync(request);
                if ((int)response.StatusCode >= 500) failures.Add($"{operation.Name} {path.Name}: {(int)response.StatusCode}");
            }
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task CookieLoginRequiresCsrfAndProtectsWritesAndLogout()
    {
        await using var host = await AcceptanceHost.StartAsync();
        using var cookies = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() })
        { BaseAddress = host.Anonymous.BaseAddress };
        var login = new { userName = "acceptance-admin", password = AcceptanceHost.Password };
        using var rejectedLogin = await cookies.PostAsJsonAsync("/api/v1/session/login", login);
        Assert.Equal(HttpStatusCode.BadRequest, rejectedLogin.StatusCode);
        await AcceptanceHost.AssertProblemAsync(rejectedLogin, 400);
        var csrf = await cookies.GetFromJsonAsync<JsonElement>("/api/v1/session/csrf");
        cookies.DefaultRequestHeaders.Add(csrf.GetProperty("headerName").GetString()!, csrf.GetProperty("token").GetString());
        using var acceptedLogin = await cookies.PostAsJsonAsync("/api/v1/session/login", login);
        Assert.Equal(HttpStatusCode.OK, acceptedLogin.StatusCode);
        var me = await cookies.GetFromJsonAsync<JsonElement>("/api/v1/session/me");
        Assert.True(me.GetProperty("authenticated").GetBoolean());
        cookies.DefaultRequestHeaders.Remove(csrf.GetProperty("headerName").GetString()!);
        using var rejectedWrite = await cookies.PutAsJsonAsync("/api/v1/settings/smtp", new { commandTimeoutSeconds = 301 });
        Assert.Equal(HttpStatusCode.BadRequest, rejectedWrite.StatusCode);
        await AcceptanceHost.AssertProblemAsync(rejectedWrite, 400);
        csrf = await cookies.GetFromJsonAsync<JsonElement>("/api/v1/session/csrf");
        cookies.DefaultRequestHeaders.Add(csrf.GetProperty("headerName").GetString()!, csrf.GetProperty("token").GetString());
        using var acceptedWrite = await cookies.PutAsJsonAsync("/api/v1/settings/smtp", new { commandTimeoutSeconds = 301 });
        Assert.Equal(HttpStatusCode.OK, acceptedWrite.StatusCode);
        using var logout = await cookies.PostAsync("/api/v1/session/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using var loggedOut = await cookies.GetAsync("/api/v1/session/me");
        Assert.Equal(HttpStatusCode.Unauthorized, loggedOut.StatusCode);
    }

    [Fact]
    public async Task DomainAccountAndAliasHttpWritesPersistAndAreAudited()
    {
        await using var host = await AcceptanceHost.StartAsync();
        var domain = await host.JsonAsync(HttpMethod.Post, "/api/v1/domains", new { name = "acceptance.invalid" }, 201);
        var domainId = domain.GetProperty("id").GetGuid();
        var path = $"/api/v1/domains/{domainId}";
        await host.JsonAsync(HttpMethod.Put, path + "/details", new
        {
            mailHostName = "mail.acceptance.invalid", smtpHostName = "mail.acceptance.invalid",
            isActive = true, outboundIpAddresses = "", directDeliverSameDomain = true
        });
        await host.JsonAsync(HttpMethod.Put, path + "/quota", new { quotaBytes = 2_000_000_000L });
        var account = await host.JsonAsync(HttpMethod.Post, path + "/accounts", new
        { localPart = "receiver", password = AcceptanceHost.Password, quotaBytes = 100_000_000L }, 201);
        var accountId = account.GetProperty("id").GetGuid();
        await host.JsonAsync(HttpMethod.Patch, $"/api/v1/accounts/{accountId}", new { isEnabled = false, quotaBytes = 110_000_000L }, 204);
        await host.JsonAsync(HttpMethod.Post, path + "/accounts/batch-enable", new { ids = new[] { accountId } }, 204);
        await host.JsonAsync(HttpMethod.Post, $"/api/v1/accounts/{accountId}/password", new { password = "ChangedAcceptance_2026" }, 204);
        var alias = await host.JsonAsync(HttpMethod.Post, path + "/aliases", new
        { sourceLocalPart = "support", targetAccountId = accountId, mode = "SendReceive" }, 201);
        var aliasId = alias.GetProperty("id").GetGuid();
        await host.JsonAsync(HttpMethod.Put, path + $"/aliases/{aliasId}", new
        { sourceLocalPart = "help", targetAccountId = accountId, mode = "ReceiveOnly" });
        var found = await host.JsonAsync(HttpMethod.Get, path + "/accounts/search?search=receiver&page=1&pageSize=20");
        Assert.Equal(1, found.GetProperty("totalCount").GetInt32());
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            var storedAccount = await db.Accounts.SingleAsync(x => x.Id == accountId);
            Assert.True(storedAccount.IsEnabled);
            Assert.Equal(110_000_000L, storedAccount.QuotaBytes);
            Assert.Equal("help", (await db.Aliases.SingleAsync(x => x.Id == aliasId)).SourceLocalPart);
            Assert.True(await db.AuditRecords.CountAsync(x => x.Actor == "acceptance-admin") >= 7);
        }
        await host.JsonAsync(HttpMethod.Delete, $"/api/v1/aliases/{aliasId}", expected: 204);
        await host.JsonAsync(HttpMethod.Delete, $"/api/v1/accounts/{accountId}", expected: 409);
        await host.JsonAsync(HttpMethod.Post, path + "/accounts/batch-disable", new { ids = new[] { accountId } }, 204);
        await host.JsonAsync(HttpMethod.Delete, $"/api/v1/accounts/{accountId}", expected: 204);
        await using var deletedDb = await host.Factory.CreateDbContextAsync();
        Assert.NotNull((await deletedDb.Accounts.IgnoreQueryFilters().SingleAsync(x => x.Id == accountId)).DeletedAt);
    }

    [Fact]
    public async Task AdminAndApiKeyLifecycleEnforcesPermissionsAndRevocation()
    {
        await using var host = await AcceptanceHost.StartAsync();
        var grants = new[] { new { permissionCode = "dashboard.read" } };
        var reader = await host.JsonAsync(HttpMethod.Post, "/api/v1/admins", new
        { userName = "reader", password = AcceptanceHost.Password, isEnabled = true, grants }, 201);
        var readerId = reader.GetProperty("id").GetGuid();
        await host.JsonAsync(HttpMethod.Get, $"/api/v1/admins/{readerId}/grants");
        await host.JsonAsync(HttpMethod.Put, $"/api/v1/admins/{readerId}/grants", new { grants });
        using var cookieClient = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() })
        { BaseAddress = host.Anonymous.BaseAddress };
        var csrf = await cookieClient.GetFromJsonAsync<JsonElement>("/api/v1/session/csrf");
        cookieClient.DefaultRequestHeaders.Add(csrf.GetProperty("headerName").GetString()!, csrf.GetProperty("token").GetString());
        using var cookieLogin = await cookieClient.PostAsJsonAsync("/api/v1/session/login", new
        { userName = "reader", password = AcceptanceHost.Password });
        Assert.Equal(HttpStatusCode.OK, cookieLogin.StatusCode);
        using var limited = await host.LoginAsync("reader");
        using var allowed = await limited.GetAsync("/api/v1/overview");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        using var forbidden = await limited.GetAsync("/api/v1/settings/delivery");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        await AcceptanceHost.AssertProblemAsync(forbidden, 403);
        using var escalation = await limited.PostAsJsonAsync("/api/v1/admins", new
        { userName = "escalated", password = AcceptanceHost.Password, isEnabled = true, grants = new[] { new { permissionCode = "*" } } });
        Assert.Equal(HttpStatusCode.Forbidden, escalation.StatusCode);
        var key = await host.JsonAsync(HttpMethod.Post, "/api/v1/api-keys", new { name = "reader-key", grants });
        var keyId = key.GetProperty("id").GetGuid();
        using var keyClient = new HttpClient { BaseAddress = host.Anonymous.BaseAddress };
        keyClient.DefaultRequestHeaders.Add("X-Api-Key", key.GetProperty("apiKey").GetString());
        using var keyAllowed = await keyClient.GetAsync("/api/v1/overview");
        Assert.Equal(HttpStatusCode.OK, keyAllowed.StatusCode);
        using var keyForbidden = await keyClient.GetAsync("/api/v1/domains");
        Assert.Equal(HttpStatusCode.Forbidden, keyForbidden.StatusCode);
        await host.JsonAsync(HttpMethod.Get, $"/api/v1/api-keys/{keyId}/grants");
        await host.JsonAsync(HttpMethod.Put, $"/api/v1/api-keys/{keyId}/grants", new { grants });
        var rotated = await host.JsonAsync(HttpMethod.Post, $"/api/v1/api-keys/{keyId}/refresh");
        using var oldKey = await keyClient.GetAsync("/api/v1/overview");
        Assert.Equal(HttpStatusCode.Unauthorized, oldKey.StatusCode);
        keyClient.DefaultRequestHeaders.Remove("X-Api-Key");
        keyClient.DefaultRequestHeaders.Add("X-Api-Key", rotated.GetProperty("apiKey").GetString());
        using var rotatedKey = await keyClient.GetAsync("/api/v1/overview");
        Assert.Equal(HttpStatusCode.OK, rotatedKey.StatusCode);
        await host.JsonAsync(HttpMethod.Delete, $"/api/v1/api-keys/{keyId}", expected: 204);
        using var removedKey = await keyClient.GetAsync("/api/v1/overview");
        Assert.Equal(HttpStatusCode.Unauthorized, removedKey.StatusCode);
        await host.JsonAsync(HttpMethod.Patch, $"/api/v1/admins/{readerId}", new { userName = "reader", isEnabled = false, password = (string?)null });
        using var disabled = await limited.GetAsync("/api/v1/overview");
        Assert.True(disabled.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"Disabled administrator's JWT returned {(int)disabled.StatusCode}");
        using var disabledCookie = await cookieClient.GetAsync("/api/v1/session/me");
        Assert.Equal(HttpStatusCode.Unauthorized, disabledCookie.StatusCode);
        await host.JsonAsync(HttpMethod.Delete, $"/api/v1/admins/{readerId}", expected: 204);
        await using var db = await host.Factory.CreateDbContextAsync();
        Assert.False(await db.ApiKeys.AnyAsync(x => x.Id == keyId));
        Assert.False(await db.AdminUsers.AnyAsync(x => x.Id == readerId));
        Assert.True(await db.AuditRecords.AnyAsync(x => x.Action == "api-key.refresh"));
    }

    [Fact]
    public async Task RecycleBinBatchPurgeReturnsPerItemResultsAndAuditsOverHttp()
    {
        await using var host = await AcceptanceHost.StartAsync();
        var domain = new MailDomain { Name = "recycle-batch.invalid", UsedBytes = 42 };
        var account = new MailAccount { Domain = domain, LocalPart = "recipient", PasswordHash = "unused", UsedBytes = 42 };
        var mailbox = new Mailbox { Account = account, Name = "Trash" };
        var message = new MailboxMessage
        {
            Mailbox = mailbox, Uid = 1,
            Message = new StoredMessage { ContentKey = new string('f', 64), EnvelopeFrom = "sender@example.net", Size = 42 }
        };
        var pending = new CleanupQueueItem
        {
            ResourceType = CleanupResourceType.MailboxMessage, ResourceId = message.Id,
            AccountId = account.Id, DomainId = domain.Id, PurgeAfter = DateTimeOffset.UtcNow.AddDays(30)
        };
        var processing = new CleanupQueueItem
        {
            ResourceType = CleanupResourceType.ContentFile, ContentKey = "processing-content",
            Status = CleanupQueueStatus.Processing, PurgeAfter = DateTimeOffset.UtcNow.AddDays(30)
        };
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            db.MailboxMessages.Add(message);
            db.CleanupQueue.AddRange(pending, processing);
            await db.SaveChangesAsync();
        }
        var list = await host.JsonAsync(HttpMethod.Get, "/api/v1/recycle-bin/search?resourceType=MailboxMessage");
        Assert.Equal("sender@example.net", list.GetProperty("items")[0].GetProperty("sender").GetString());
        Assert.Equal("（主题不可读取）", list.GetProperty("items")[0].GetProperty("subject").GetString());
        using (var anonymous = await host.Anonymous.PostAsJsonAsync("/api/v1/recycle-bin/batch/purge", new { ids = new[] { pending.Id } }))
            await AcceptanceHost.AssertProblemAsync(anonymous, 401);
        await host.JsonAsync(HttpMethod.Post, "/api/v1/admins", new
        {
            userName = "recycle-reader", password = AcceptanceHost.Password, isEnabled = true,
            grants = new[] { new { permissionCode = "task.read" } }
        }, 201);
        using (var reader = await host.LoginAsync("recycle-reader"))
        using (var forbidden = await reader.PostAsJsonAsync("/api/v1/recycle-bin/batch/purge", new { ids = new[] { pending.Id } }))
            await AcceptanceHost.AssertProblemAsync(forbidden, 403);
        using (var invalid = await host.Admin.PostAsJsonAsync("/api/v1/recycle-bin/batch/purge", new { ids = new[] { pending.Id, pending.Id } }))
            await AcceptanceHost.AssertProblemAsync(invalid, 400);
        var result = await host.JsonAsync(HttpMethod.Post, "/api/v1/recycle-bin/batch/purge",
            new { ids = new[] { pending.Id, processing.Id, Guid.NewGuid() } });
        Assert.Equal("deleted", result.GetProperty("results")[0].GetProperty("status").GetString());
        Assert.Equal("conflict", result.GetProperty("results")[1].GetProperty("status").GetString());
        Assert.Equal("notFound", result.GetProperty("results")[2].GetProperty("status").GetString());
        await using var verified = await host.Factory.CreateDbContextAsync();
        Assert.Equal(CleanupQueueStatus.Completed, (await verified.CleanupQueue.SingleAsync(x => x.Id == pending.Id)).Status);
        Assert.Equal(CleanupQueueStatus.Processing, (await verified.CleanupQueue.SingleAsync(x => x.Id == processing.Id)).Status);
        Assert.True(await verified.AuditRecords.AnyAsync(x => x.Action == "recycle.purge" && x.Actor == "acceptance-admin"));
    }

    [Fact]
    public async Task QueueHttpRejectsActiveLeaseAndReturnsPerItemBatchResults()
    {
        await using var host = await AcceptanceHost.StartAsync();
        var message = new StoredMessage
        {
            ContentKey = "acceptance-message", EnvelopeFrom = "sender@acceptance.invalid", Size = 42
        };
        var failed = new DeliveryJob
        {
            Message = message, Sender = "sender@acceptance.invalid",
            Recipient = "remote@example.invalid", Status = DeliveryStatus.Failed, Attempts = 2
        };
        var leased = new DeliveryJob
        {
            Message = message, Sender = "sender@acceptance.invalid",
            Recipient = "other@example.invalid", Status = DeliveryStatus.Processing,
            Attempts = 1, LeaseOwner = "acceptance-worker", LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(5)
        };
        var concurrent = new DeliveryJob
        {
            Message = message, Sender = "sender@acceptance.invalid",
            Recipient = "parallel@example.invalid", Status = DeliveryStatus.Failed, Attempts = 3
        };
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            db.Messages.Add(message);
            db.DeliveryJobs.AddRange(failed, leased, concurrent);
            await db.SaveChangesAsync();
        }
        await host.JsonAsync(HttpMethod.Get, "/api/v1/queues?page=1&pageSize=20");
        await host.JsonAsync(HttpMethod.Get, "/api/v1/queues/summary");
        await host.JsonAsync(HttpMethod.Post, $"/api/v1/queues/{failed.Id}/retry", new { }, 400);
        await host.JsonAsync(HttpMethod.Delete, $"/api/v1/queues/{leased.Id}", expected: 409);
        var simultaneous = await Task.WhenAll(
            host.Admin.PostAsJsonAsync($"/api/v1/queues/{concurrent.Id}/retry", new { expectedAttempts = 3 }),
            host.Admin.PostAsJsonAsync($"/api/v1/queues/{concurrent.Id}/retry", new { expectedAttempts = 3 }));
        try
        {
            Assert.Equal(new[] { 202, 409 }, simultaneous.Select(response => (int)response.StatusCode).Order().ToArray());
        }
        finally
        {
            foreach (var response in simultaneous) response.Dispose();
        }
        var version = new Dictionary<Guid, int> { [failed.Id] = 2, [leased.Id] = 1 };
        var retry = await host.JsonAsync(HttpMethod.Post, "/api/v1/queues/batch/retry",
            new { ids = new[] { failed.Id, leased.Id }, expectedAttempts = version });
        Assert.Equal(1, retry.GetProperty("succeeded").GetInt32());
        Assert.Equal("Conflict", retry.GetProperty("items")[1].GetProperty("status").GetString());
        var repeated = await host.JsonAsync(HttpMethod.Post, "/api/v1/queues/batch/retry",
            new { ids = new[] { failed.Id }, expectedAttempts = new Dictionary<Guid, int> { [failed.Id] = 2 } });
        Assert.Equal("Conflict", repeated.GetProperty("items")[0].GetProperty("status").GetString());
        var deletion = await host.JsonAsync(HttpMethod.Post, "/api/v1/queues/batch/delete",
            new { ids = new[] { failed.Id, leased.Id } });
        Assert.Equal("Succeeded", deletion.GetProperty("items")[0].GetProperty("status").GetString());
        Assert.Equal("Conflict", deletion.GetProperty("items")[1].GetProperty("status").GetString());
        await using var verified = await host.Factory.CreateDbContextAsync();
        Assert.False(await verified.DeliveryJobs.AnyAsync(x => x.Id == failed.Id));
        Assert.True(await verified.DeliveryJobs.AnyAsync(x => x.Id == leased.Id));
        Assert.True(await verified.AuditRecords.AnyAsync(x => x.Action == "queue.batch-retry"));
        Assert.True(await verified.AuditRecords.AnyAsync(x => x.Action == "queue.batch-delete"));
    }

    [Fact]
    public async Task SettingsFirewallAndAuditHttpWorkflowsPersist()
    {
        await using var host = await AcceptanceHost.StartAsync();
        foreach (var section in new[] { "delivery", "smtp", "mailboxes", "message-templates", "certificates" })
            await host.JsonAsync(HttpMethod.Get, "/api/v1/settings/" + section);
        await host.JsonAsync(HttpMethod.Put, "/api/v1/settings/delivery", new
        { batchSize = 11, leaseSeconds = 120, maximumAttempts = 8, pollIntervalSeconds = 7 });
        await host.JsonAsync(HttpMethod.Put, "/api/v1/settings/smtp", new { commandTimeoutSeconds = 222 });
        await host.JsonAsync(HttpMethod.Put, "/api/v1/settings/mailboxes", new { defaultFolders = new[] { "INBOX", "Sent", "QA" } });
        await host.JsonAsync(HttpMethod.Put, "/api/v1/settings/certificates", new { defaultMailHostName = (string?)null, allowSingleCertificateFallback = false });
        var defaults = await host.JsonAsync(HttpMethod.Get, "/api/v1/settings/message-templates/defaults");
        await host.JsonAsync(HttpMethod.Put, "/api/v1/settings/message-templates", defaults);
        await host.JsonAsync(HttpMethod.Post, "/api/v1/settings/message-templates/preview", new { subject = "QA", body = "<p>Safe preview</p>" });
        await host.JsonAsync(HttpMethod.Post, "/api/v1/settings/message-templates/preview", new { subject = "QA", body = "<script>alert(1)</script>" }, 400);
        var saved = await host.JsonAsync(HttpMethod.Get, "/api/v1/settings/smtp");
        Assert.Equal(222, saved.GetProperty("commandTimeoutSeconds").GetInt32());
        var firewall = await host.JsonAsync(HttpMethod.Post, "/api/v1/firewall/rules", new { cidr = "192.0.2.19/32", note = "acceptance" }, 201);
        var firewallId = firewall.GetProperty("id").GetGuid();
        await host.JsonAsync(HttpMethod.Post, "/api/v1/firewall/rules", new { cidr = "192.0.2.19/32", note = "duplicate" }, 409);
        await host.JsonAsync(HttpMethod.Put, $"/api/v1/firewall/rules/{firewallId}", new { isEnabled = false, note = "updated" });
        await host.JsonAsync(HttpMethod.Get, "/api/v1/firewall/rules/search?search=updated&page=1&pageSize=20");
        await host.JsonAsync(HttpMethod.Delete, $"/api/v1/firewall/rules/{firewallId}", expected: 204);
        var audit = await host.JsonAsync(HttpMethod.Get, "/api/v1/audit?page=1&pageSize=100");
        Assert.True(audit.GetProperty("totalCount").GetInt32() >= 5);
        var auditId = audit.GetProperty("items")[0].GetProperty("id").GetGuid();
        await host.JsonAsync(HttpMethod.Get, $"/api/v1/audit/{auditId}");
        using var export = await host.Admin.PostAsJsonAsync("/api/v1/audit/export", new { ids = new[] { auditId } });
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.NotNull(export.Content.Headers.ContentDisposition);
        Assert.DoesNotContain(AcceptanceHost.Password, await export.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AccountDirectorySearchPermissionsRebindingAndPersistence()
    {
        await using var host = await AcceptanceHost.StartAsync();
        var domain = await host.JsonAsync(HttpMethod.Post, "/api/v1/domains", new { name = "directory.invalid" }, 201);
        var id = domain.GetProperty("id").GetGuid();
        var root = $"/api/v1/domains/{id}";
        var account = await host.JsonAsync(HttpMethod.Post, root + "/accounts", new { localPart = "owner", password = AcceptanceHost.Password, quotaBytes = 1073741824L }, 201);
        var accountId = account.GetProperty("id").GetGuid();
        Guid secondId;
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            var second = new MailAccount { DomainId = id, LocalPart = "second", PasswordHash = "unused" };
            secondId = second.Id;
            db.Accounts.Add(second);
            for (var i = 0; i < 24; i++) db.Accounts.Add(new MailAccount { DomainId = id, LocalPart = $"paging{i:00}", PasswordHash = "unused" });
            db.Aliases.Add(new MailAlias { DomainId = id, SourceLocalPart = "orphan", TargetAccountId = null });
            await db.SaveChangesAsync();
        }
        var first = await host.JsonAsync(HttpMethod.Post, root + "/aliases", new { sourceLocalPart = "sales-one", targetAccountId = accountId, mode = "SendReceive" }, 201);
        await host.JsonAsync(HttpMethod.Post, root + "/aliases", new { sourceLocalPart = "sales-two", targetAccountId = accountId, mode = "ReceiveOnly" }, 201);
        var result = await host.JsonAsync(HttpMethod.Get, root + "/account-directory/search?query=sales&page=1&pageSize=5");
        Assert.Equal(1, result.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, result.GetProperty("items")[0].GetProperty("matchedAliasCount").GetInt32());
        Assert.Equal(accountId, result.GetProperty("items")[0].GetProperty("id").GetGuid());
        var next = await host.JsonAsync(HttpMethod.Get, root + "/account-directory/search?page=2&pageSize=20");
        Assert.Equal(26, next.GetProperty("totalCount").GetInt32());
        Assert.Equal(6, next.GetProperty("items").GetArrayLength());
        var late = await host.JsonAsync(HttpMethod.Get, root + "/account-directory/search?query=paging23");
        Assert.Equal(1, late.GetProperty("totalCount").GetInt32());
        var pending = await host.JsonAsync(HttpMethod.Get, root + "/account-directory/aliases?pending=true");
        Assert.Equal(1, pending.GetProperty("totalCount").GetInt32());
        var orphanId = pending.GetProperty("items")[0].GetProperty("id").GetGuid();
        await host.JsonAsync(HttpMethod.Put, root + $"/aliases/{orphanId}", new { targetAccountId = secondId, mode = "ReceiveOnly" });
        pending = await host.JsonAsync(HttpMethod.Get, root + "/account-directory/aliases?pending=true");
        Assert.Equal(0, pending.GetProperty("totalCount").GetInt32());
        await host.JsonAsync(HttpMethod.Put, root + $"/aliases/{first.GetProperty("id").GetGuid()}", new { targetAccountId = secondId });
        var related = await host.JsonAsync(HttpMethod.Get, root + $"/account-directory/aliases?accountId={accountId}");
        Assert.Equal(1, related.GetProperty("totalCount").GetInt32());
        await host.JsonAsync(HttpMethod.Get, root + $"/account-directory/accounts/{Guid.NewGuid()}", expected: 404);
        var other = await host.JsonAsync(HttpMethod.Post, "/api/v1/domains", new { name = "boundary.invalid" }, 201);
        var otherId = other.GetProperty("id").GetGuid();
        await host.JsonAsync(HttpMethod.Get, $"/api/v1/domains/{otherId}/account-directory/accounts/{accountId}", expected: 404);
        await host.JsonAsync(HttpMethod.Post, $"/api/v1/domains/{otherId}/aliases", new { sourceLocalPart = "cross", targetAccountId = accountId, mode = "SendReceive" }, 409);
        foreach (var kind in new[] { "account", "alias" })
        {
            await host.JsonAsync(HttpMethod.Post, "/api/v1/admins", new { userName = kind + "-reader", password = AcceptanceHost.Password, isEnabled = true,
                grants = new[] { new { permissionCode = $"domain.{kind}.read", resourceType = "domain", resourceKey = id.ToString() } } }, 201);
            using var reader = await host.LoginAsync(kind + "-reader");
            var visibleDomains = await reader.GetFromJsonAsync<JsonElement>("/api/v1/account-directories");
            Assert.Equal(1, visibleDomains.GetProperty("totalCount").GetInt32());
            Assert.Equal(id, visibleDomains.GetProperty("items")[0].GetProperty("id").GetGuid());
            var search = await reader.GetFromJsonAsync<JsonElement>(root + "/account-directory/search?query=sales");
            Assert.Equal(kind == "account" ? 0 : 2, search.GetProperty("totalCount").GetInt32());
            var detail = await reader.GetFromJsonAsync<JsonElement>(root + $"/account-directory/accounts/{accountId}");
            if (kind == "alias") Assert.Equal(JsonValueKind.Null, detail.GetProperty("quotaBytes").ValueKind);
            else Assert.Equal(JsonValueKind.Null, detail.GetProperty("aliasCount").ValueKind);
            using var forbidden = await reader.PatchAsJsonAsync($"/api/v1/accounts/{accountId}", new { isEnabled = false });
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            using var boundary = await reader.GetAsync($"/api/v1/domains/{otherId}/account-directory/search");
            Assert.Equal(HttpStatusCode.Forbidden, boundary.StatusCode);
            using var aliases = await reader.GetAsync(root + "/account-directory/aliases");
            Assert.Equal(kind == "alias" ? HttpStatusCode.OK : HttpStatusCode.Forbidden, aliases.StatusCode);
        }
        await using (var db = await host.Factory.CreateDbContextAsync())
        {
            Assert.Equal(secondId, (await db.Aliases.SingleAsync(x => x.Id == orphanId)).TargetAccountId);
            Assert.True(await db.AuditRecords.AnyAsync(x => x.Action == "alias.update" && x.Resource == "alias:" + orphanId));
        }
        var audit = await host.JsonAsync(HttpMethod.Get, "/api/v1/audit?page=1&pageSize=100");
        Assert.True(audit.GetProperty("totalCount").GetInt32() > 0);
    }

    private sealed class AcceptanceHost : IAsyncDisposable
    {
        public const string Password = "AcceptanceOnly_2026";
        private readonly ServiceProvider services;
        private readonly Process process;
        private readonly List<string> logs = [];
        public HttpClient Anonymous { get; }
        public HttpClient Admin { get; }
        public IDbContextFactory<MailDbContext> Factory => services.GetRequiredService<IDbContextFactory<MailDbContext>>();

        private AcceptanceHost(ServiceProvider services, Process process, Uri address)
        {
            this.services = services;
            this.process = process;
            Anonymous = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(30) };
            Admin = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(30) };
            process.OutputDataReceived += (_, args) => { if (args.Data is { } line) lock (logs) logs.Add(line); };
            process.ErrorDataReceived += (_, args) => { if (args.Data is { } line) lock (logs) logs.Add(line); };
        }

        public Task<ResourceSettingsView> SaveResourceProfileAsync(string id) =>
            services.GetRequiredService<ResourceSettingsService>().SaveAsync(id, "independent-process", CancellationToken.None);

        public static async Task<AcceptanceHost> StartAsync(bool runAll = false)
        {
            var root = Path.Combine(Path.GetTempPath(), "mailserver-http-acceptance-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var values = new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["ConnectionStrings:MailDatabase"] = $"Data Source={Path.Combine(root, "acceptance.db")}",
                ["Storage:RootPath"] = Path.Combine(root, "messages"),
                ["SecurityAssets:RootPath"] = Path.Combine(root, "security"),
                ["AdminServer:BindAddress"] = "127.0.0.1", ["AdminServer:Http:Enabled"] = "true",
                ["AdminServer:Http:Port"] = port.ToString(), ["AdminServer:Https:Enabled"] = "false",
                ["Jwt:SigningKey"] = "acceptance-only-signing-key-" + Guid.NewGuid().ToString("N"),
                ["RunAll:Enabled"] = runAll.ToString(), ["MailServerLogging:File:Enabled"] = "false",
                ["Smtp:Endpoints:0:Address"] = "127.0.0.1", ["Smtp:Endpoints:0:Port"] = "0",
                ["Smtp:Endpoints:0:Security"] = "Plain",
                ["Imap:Endpoints:0:Address"] = "127.0.0.1", ["Imap:Endpoints:0:Port"] = "0",
                ["Imap:Endpoints:0:Security"] = "Plain",
                ["Logging:LogLevel:Default"] = "Warning"
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            var collection = new ServiceCollection().AddSingleton<IConfiguration>(config).AddLogging().AddMailServerInfrastructure(config);
            var services = collection.BuildServiceProvider();
            await using (var db = await services.GetRequiredService<IDbContextFactory<MailDbContext>>().CreateDbContextAsync())
                await db.Database.MigrateAsync();
            await services.GetRequiredService<AdminApplicationService>().CreateAdminAsync("acceptance-admin", Password, CancellationToken.None);
            var configPath = Path.Combine(root, "mailserver.json");
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(values));
            var start = new ProcessStartInfo("dotnet")
            { WorkingDirectory = root, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add(typeof(AdminApiProblems).Assembly.Location);
            start.Environment[PortableConfiguration.EnvironmentVariable] = configPath;
            var process = new Process { StartInfo = start };
            var host = new AcceptanceHost(services, process, new Uri($"http://127.0.0.1:{port}"));
            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                for (var i = 0; i < 100; i++)
                {
                    if (process.HasExited) throw new InvalidOperationException(string.Join(Environment.NewLine, host.logs));
                    try
                    {
                        using var ready = await host.Anonymous.GetAsync("/api/v1/health/ready");
                        if (ready.IsSuccessStatusCode) break;
                    }
                    catch (HttpRequestException) { }
                    await Task.Delay(100);
                }
                using var login = await host.Anonymous.PostAsJsonAsync("/api/v1/auth/token", new { userName = "acceptance-admin", password = Password });
                login.EnsureSuccessStatusCode();
                var token = await login.Content.ReadFromJsonAsync<JsonElement>();
                host.Admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.GetProperty("accessToken").GetString());
                return host;
            }
            catch { await host.DisposeAsync(); throw; }
        }

        public async Task<JsonElement> JsonAsync(HttpMethod method, string url, object? body = null, int expected = 200)
        {
            using var request = new HttpRequestMessage(method, url);
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await Admin.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            Assert.True((int)response.StatusCode == expected, $"{method} {url}: expected {expected}, got {(int)response.StatusCode}: {text}");
            return string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
        }

        public async Task<HttpClient> LoginAsync(string userName)
        {
            using var login = await Anonymous.PostAsJsonAsync("/api/v1/auth/token", new { userName, password = Password });
            login.EnsureSuccessStatusCode();
            var token = await login.Content.ReadFromJsonAsync<JsonElement>();
            var client = new HttpClient { BaseAddress = Anonymous.BaseAddress };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.GetProperty("accessToken").GetString());
            return client;
        }

        public static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, int status)
        {
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(status, problem.GetProperty("status").GetInt32());
            Assert.False(string.IsNullOrEmpty(problem.GetProperty("traceId").GetString()));
            return problem;
        }

        public async ValueTask DisposeAsync()
        {
            if (process.Id != 0 && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            process.Dispose();
            Anonymous.Dispose();
            Admin.Dispose();
            await services.DisposeAsync();
        }
    }
}
