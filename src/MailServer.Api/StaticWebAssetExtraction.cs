using MailServer.Api.Components;
using Microsoft.Extensions.FileProviders;

namespace MailServer.Api;

public static class StaticWebAssetExtraction
{
    private const string ResourcePrefix = "MailServer.Api.StaticWebAssets/wwwroot/";

    public static void ExtractBundledStaticWebAssets(this WebApplication app)
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        app.Environment.WebRootPath = webRoot;

        Directory.CreateDirectory(webRoot);
        app.Environment.WebRootFileProvider = new PhysicalFileProvider(webRoot);
        var webRootFullPath = Path.GetFullPath(webRoot);
        var webRootWithSeparator = webRootFullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var assembly = typeof(App).Assembly;

        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            var relativePath = resourceName[ResourcePrefix.Length..]
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            var targetPath = Path.GetFullPath(Path.Combine(webRootFullPath, relativePath));
            if (!targetPath.StartsWith(webRootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var source = assembly.GetManifestResourceStream(resourceName);
            if (source is null)
            {
                continue;
            }

            if (File.Exists(targetPath) && new FileInfo(targetPath).Length == source.Length)
            {
                continue;
            }

            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = targetPath + ".tmp";
            using (var target = File.Create(temporaryPath))
            {
                source.CopyTo(target);
            }

            File.Move(temporaryPath, targetPath, overwrite: true);
        }
    }
}
