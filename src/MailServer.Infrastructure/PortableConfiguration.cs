using Microsoft.Extensions.Configuration;

namespace MailServer.Infrastructure;

public static class PortableConfiguration
{
    public const string EnvironmentVariable = "MAILSERVER_CONFIG";

    public static void AddPortableConfiguration(this ConfigurationManager configuration)
    {
        var configuredPath = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            var defaultPath = Path.Combine(Environment.CurrentDirectory, "mailserver.json");
            if (File.Exists(defaultPath))
            {
                configuredPath = defaultPath;
            }
        }

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var fullPath = Path.GetFullPath(configuredPath);
            Directory.SetCurrentDirectory(Path.GetDirectoryName(fullPath)!);
            configuration.AddJsonFile(fullPath, optional: false, reloadOnChange: true);
            configuration.AddEnvironmentVariables();
        }
    }
}
