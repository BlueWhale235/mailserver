using MailServer.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace MailServer.Tests;

public sealed class AdminSpaHostingTests
{
    [Fact]
    public void ValidateAdminBundle_AcceptsCompleteBundle()
    {
        using var bundle = AdminBundle.Create(includeAssets: true);

        StaticWebAssetExtraction.ValidateAdminBundle(bundle.WebRoot);
    }

    [Fact]
    public void ValidateAdminBundle_ReportsMissingReferencedAssets()
    {
        using var bundle = AdminBundle.Create(includeAssets: false);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            StaticWebAssetExtraction.ValidateAdminBundle(bundle.WebRoot));

        Assert.Contains("/admin/assets/app.js", exception.Message, StringComparison.Ordinal);
        Assert.Contains("/admin/assets/app.css", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdminSpaFallback_Returns404ForMissingFilesAndHtmlForClientRoutes()
    {
        using var bundle = AdminBundle.Create(includeAssets: true);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.Environment.WebRootPath = bundle.WebRoot;
        app.Environment.WebRootFileProvider = new PhysicalFileProvider(bundle.WebRoot);
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = app.Environment.WebRootFileProvider
        });
        app.UseRouting();
        AdminWebHosting.MapAdminSpaFallback(app);
        await app.StartAsync();

        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses;
        var address = Assert.Single(addresses!);
        using var client = new HttpClient { BaseAddress = new Uri(address) };

        var script = await client.GetAsync("/admin/assets/app.js");
        var missingScript = await client.GetAsync("/admin/assets/missing.js");
        var clientRoute = await client.GetAsync("/admin/domains/example.test");

        Assert.Equal(System.Net.HttpStatusCode.OK, script.StatusCode);
        Assert.Equal("text/javascript", script.Content.Headers.ContentType?.MediaType);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, missingScript.StatusCode);
        Assert.Equal("text/html", clientRoute.Content.Headers.ContentType?.MediaType);
        await app.StopAsync();
    }

    private sealed class AdminBundle : IDisposable
    {
        private AdminBundle(string webRoot) => WebRoot = webRoot;

        public string WebRoot { get; }

        public static AdminBundle Create(bool includeAssets)
        {
            var webRoot = Path.Combine(Path.GetTempPath(), $"mailserver-admin-bundle-{Guid.NewGuid():N}", "wwwroot");
            var adminRoot = Path.Combine(webRoot, "admin");
            var assetsRoot = Path.Combine(adminRoot, "assets");
            Directory.CreateDirectory(assetsRoot);
            File.WriteAllText(
                Path.Combine(adminRoot, "index.html"),
                """
                <!doctype html>
                <script type="module" src="/admin/assets/app.js"></script>
                <link rel="stylesheet" href="/admin/assets/app.css">
                """);

            if (includeAssets)
            {
                File.WriteAllText(Path.Combine(assetsRoot, "app.js"), "export {};");
                File.WriteAllText(Path.Combine(assetsRoot, "app.css"), "body {}");
            }

            return new AdminBundle(webRoot);
        }

        public void Dispose()
        {
            var root = Directory.GetParent(WebRoot)?.FullName;
            if (root is not null && Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
