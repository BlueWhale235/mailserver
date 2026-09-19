using Microsoft.Extensions.FileProviders;
using System.Text.RegularExpressions;

namespace MailServer.Api;

public static partial class StaticWebAssetExtraction
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
        var assembly = typeof(StaticWebAssetExtraction).Assembly;

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

        ValidateAdminBundle(webRootFullPath);
    }

    internal static void ValidateAdminBundle(string webRoot)
    {
        var indexPath = Path.Combine(webRoot, "admin", "index.html");
        if (!File.Exists(indexPath))
        {
            throw new InvalidOperationException(
                $"The Vue administration bundle is incomplete: '{indexPath}' is missing. Rebuild and redeploy the complete package.");
        }

        var index = File.ReadAllText(indexPath);
        var missingAssets = AdminAssetReferenceRegex().Matches(index)
            .Select(match => match.Groups["path"].Value)
            .Select(path => path.Split('?', '#')[0])
            .Distinct(StringComparer.Ordinal)
            .Where(path => !File.Exists(Path.Combine(
                webRoot,
                path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar))))
            .ToArray();

        if (missingAssets.Length > 0)
        {
            throw new InvalidOperationException(
                $"The Vue administration bundle is incomplete. Missing assets: {string.Join(", ", missingAssets)}. Rebuild and redeploy the complete package.");
        }
    }

    [GeneratedRegex("(?:src|href)=[\"'](?<path>/admin/[^\"'#]+)[\"']", RegexOptions.IgnoreCase)]
    private static partial Regex AdminAssetReferenceRegex();
}
