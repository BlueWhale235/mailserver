using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using MailServer.Domain;
using MailServer.Infrastructure;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

return await MailServerCli.RunAsync(args);

internal static class MailServerCli
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var parsed = CliArguments.Parse(args);
            if (parsed.Positionals.Count == 0 || parsed.Positionals[0] is "help" or "--help" or "-h")
            {
                PrintHelp();
                return 0;
            }

            var command = parsed.Positionals[0].ToLowerInvariant();
            return command switch
            {
                "init" => await InitializeAsync(parsed),
                "doctor" => await DoctorAsync(parsed),
                "config" => await ConfigAsync(parsed),
                "db" => await DatabaseAsync(parsed),
                "admin" => await AdminAsync(parsed),
                "domain" => await DomainAsync(parsed),
                "account" => await AccountAsync(parsed),
                "run" => await RunHostAsync(parsed),
                _ => Fail($"Unknown command '{command}'.")
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> InitializeAsync(CliArguments args)
    {
        var path = GetConfigPath(args);
        if (File.Exists(path) && !args.HasFlag("force"))
            return Fail($"Configuration already exists: {path}. Use --force to replace it.");

        var interactive = !args.HasFlag("non-interactive") && !Console.IsInputRedirected;
        var providerText = args.Get("provider")
            ?? Prompt(interactive, "Database provider (Sqlite/PostgreSql/MySql)", "Sqlite");
        if (!Enum.TryParse<DatabaseProvider>(providerText, true, out var provider))
            return Fail("Database provider must be Sqlite, PostgreSql, or MySql.");
        var connection = args.Get("connection")
            ?? Prompt(interactive, "Database connection string", DefaultConnection(provider));
        var bind = args.Get("bind") ?? Prompt(interactive, "Admin bind address", "127.0.0.1");
        var httpPort = ParsePort(args.Get("http-port") ?? Prompt(interactive, "Admin HTTP port (0 disables)", "8080"), allowZero: true);
        var httpsPort = ParsePort(args.Get("https-port") ?? Prompt(interactive, "Admin HTTPS port (0 disables)", "0"), allowZero: true);
        var certificatePath = args.Get("admin-cert") ?? Prompt(interactive, "Admin PEM certificate path (blank for none)", string.Empty);
        var privateKeyPath = args.Get("admin-key") ?? Prompt(interactive, "Admin PEM private key path (blank for none)", string.Empty);
        var mailCertificatePath = args.Get("mail-cert") ?? Prompt(interactive, "Mail PEM certificate path (blank for none)", string.Empty);
        var mailPrivateKeyPath = args.Get("mail-key") ?? Prompt(interactive, "Mail PEM private key path (blank for none)", string.Empty);
        if (httpsPort > 0 && (string.IsNullOrWhiteSpace(certificatePath) || string.IsNullOrWhiteSpace(privateKeyPath)))
            return Fail("HTTPS requires --admin-cert and --admin-key.");
        if (string.IsNullOrWhiteSpace(mailCertificatePath) != string.IsNullOrWhiteSpace(mailPrivateKeyPath))
            return Fail("Mail TLS requires both --mail-cert and --mail-key.");
        if (httpPort == 0 && httpsPort == 0) return Fail("At least one admin endpoint must be enabled.");

        var adminName = args.Get("admin") ?? Prompt(interactive, "Initial administrator", "admin");
        var adminPassword = args.Get("admin-password") ?? GenerateSecret(24);
        var jwtKey = GenerateSecret(48);
        var config = CreateConfiguration(
            provider,
            connection,
            bind,
            httpPort,
            httpsPort,
            certificatePath,
            privateKeyPath,
            mailCertificatePath,
            mailPrivateKeyPath,
            jwtKey);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(config, JsonOptions));
        ProtectConfigurationFile(path);
        Environment.SetEnvironmentVariable(PortableConfiguration.EnvironmentVariable, path);
        var configuration = LoadConfiguration(path);
        await using var services = BuildServices(configuration);
        await ApplyMigrationsAsync(services);
        var administration = services.GetRequiredService<AdminApplicationService>();
        await administration.CreateAdminAsync(adminName, adminPassword, CancellationToken.None);

        Console.WriteLine($"Configuration created: {path}");
        Console.WriteLine($"Administrator: {adminName}");
        Console.WriteLine($"One-time password: {adminPassword}");
        Console.WriteLine("Save the password now; it is not stored in plaintext.");
        return 0;
    }

    private static async Task<int> DoctorAsync(CliArguments args)
    {
        var path = GetExistingConfigPath(args);
        var configuration = LoadConfiguration(path);
        var errors = ValidateConfiguration(configuration).ToList();
        foreach (var error in errors) Console.WriteLine($"[FAIL] {error}");
        if (errors.Count > 0) return 1;

        var certificates = configuration.GetSection(CertificatesOptions.SectionName).Get<CertificatesOptions>()
            ?? new CertificatesOptions();
        CheckCertificate(certificates.Mail, "Mail");
        CheckCertificate(certificates.Admin, "Admin");
        var smtpProbes = configuration.GetSection("Smtp:Endpoints").Get<List<EndpointProbe>>() ?? [];
        var imapProbes = configuration.GetSection("Imap:Endpoints").Get<List<EndpointProbe>>() ?? [];
        if (!certificates.Mail.IsConfigured
            && (smtpProbes.Any(x => x.ImplicitTls || x.RequireAuthentication)
                || imapProbes.Any(x => x.ImplicitTls)))
            Console.WriteLine("[INFO] Mail TLS is not configured; implicit TLS endpoints are disabled and authentication is unavailable.");

        var adminServer = configuration.GetSection(AdminServerOptions.SectionName).Get<AdminServerOptions>()
            ?? new AdminServerOptions();
        if (adminServer.Http.Enabled) CheckPort(adminServer.BindAddress, adminServer.Http.Port, "Admin HTTP");
        if (adminServer.Https.Enabled) CheckPort(adminServer.BindAddress, adminServer.Https.Port, "Admin HTTPS");
        foreach (var endpoint in configuration.GetSection("Smtp:Endpoints").Get<List<EndpointProbe>>() ?? [])
            CheckPort(endpoint.Address, endpoint.Port, "SMTP");
        foreach (var endpoint in configuration.GetSection("Imap:Endpoints").Get<List<EndpointProbe>>() ?? [])
            CheckPort(endpoint.Address, endpoint.Port, "IMAP");

        await using var services = BuildServices(configuration);
        var factory = services.GetRequiredService<IDbContextFactory<MailDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        if (!await db.Database.CanConnectAsync())
        {
            Console.WriteLine("[FAIL] Database connection failed.");
            return 1;
        }
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToArray();
        Console.WriteLine(pending.Length == 0
            ? "[OK] Database is reachable and schema is current."
            : $"[FAIL] Database has {pending.Length} pending migration(s).");
        Console.WriteLine("[OK] Configuration validation completed.");
        return pending.Length == 0 ? 0 : 1;
    }

    private static Task<int> ConfigAsync(CliArguments args)
    {
        if (args.Positionals.ElementAtOrDefault(1)?.ToLowerInvariant() != "validate")
            return Task.FromResult(Fail("Usage: mailserver config validate"));
        var configuration = LoadConfiguration(GetExistingConfigPath(args));
        var errors = ValidateConfiguration(configuration).ToArray();
        if (errors.Length == 0)
        {
            Console.WriteLine("Configuration is valid.");
            return Task.FromResult(0);
        }
        foreach (var error in errors) Console.Error.WriteLine(error);
        return Task.FromResult(1);
    }

    private static async Task<int> DatabaseAsync(CliArguments args)
    {
        var action = args.Positionals.ElementAtOrDefault(1)?.ToLowerInvariant();
        var configuration = LoadConfiguration(GetExistingConfigPath(args));
        await using var services = BuildServices(configuration);
        var factory = services.GetRequiredService<IDbContextFactory<MailDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        if (action == "update")
        {
            await db.Database.MigrateAsync();
            Console.WriteLine("Database schema updated.");
            return 0;
        }
        if (action == "status")
        {
            Console.WriteLine($"Provider: {configuration["Database:Provider"]}");
            Console.WriteLine($"Reachable: {await db.Database.CanConnectAsync()}");
            var applied = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
            var pending = (await db.Database.GetPendingMigrationsAsync()).ToArray();
            Console.WriteLine($"Applied migrations: {applied.Length}");
            Console.WriteLine($"Pending migrations: {pending.Length}");
            foreach (var migration in pending) Console.WriteLine($"  {migration}");
            return pending.Length == 0 ? 0 : 2;
        }
        return Fail("Usage: mailserver db status|update");
    }

    private static async Task<int> AdminAsync(CliArguments args)
    {
        var action = args.Positionals.ElementAtOrDefault(1)?.ToLowerInvariant();
        var userName = args.Positionals.ElementAtOrDefault(2)
            ?? throw new ArgumentException("Administrator name is required.");
        var password = args.Get("password") ?? args.Positionals.ElementAtOrDefault(3)
            ?? throw new ArgumentException("Use --password <value>.");
        await using var services = BuildServices(LoadConfiguration(GetExistingConfigPath(args)));
        var administration = services.GetRequiredService<AdminApplicationService>();
        if (action == "create")
        {
            var id = await administration.CreateAdminAsync(userName, password, CancellationToken.None);
            Console.WriteLine($"Administrator created: {id}");
            return 0;
        }
        if (action == "password")
        {
            var factory = services.GetRequiredService<IDbContextFactory<MailDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var id = await db.AdminUsers.Where(x => x.UserName == userName.ToLowerInvariant())
                .Select(x => (Guid?)x.Id).SingleOrDefaultAsync()
                ?? throw new KeyNotFoundException("Administrator not found.");
            await administration.ChangeAdminPasswordAsync(
                id,
                null,
                password,
                false,
                "cli",
                CancellationToken.None);
            Console.WriteLine("Administrator password updated.");
            return 0;
        }
        return Fail("Usage: mailserver admin create|password <name> --password <value>");
    }

    private static async Task<int> DomainAsync(CliArguments args)
    {
        if (args.Positionals.ElementAtOrDefault(1)?.ToLowerInvariant() != "add")
            return Fail("Usage: mailserver domain add <domain> [--mode Relay|DirectMx]");
        var name = args.Positionals.ElementAtOrDefault(2) ?? throw new ArgumentException("Domain is required.");
        var mode = Enum.Parse<OutboundMode>(args.Get("mode") ?? "Relay", true);
        await using var services = BuildServices(LoadConfiguration(GetExistingConfigPath(args)));
        var domain = await services.GetRequiredService<AdminApplicationService>()
            .CreateDomainAsync(name, mode, null, null, "cli", CancellationToken.None);
        Console.WriteLine($"Domain created: {domain.Name}");
        return 0;
    }

    private static async Task<int> AccountAsync(CliArguments args)
    {
        var action = args.Positionals.ElementAtOrDefault(1)?.ToLowerInvariant();
        var address = args.Positionals.ElementAtOrDefault(2) ?? throw new ArgumentException("Account address is required.");
        await using var services = BuildServices(LoadConfiguration(GetExistingConfigPath(args)));
        var administration = services.GetRequiredService<AdminApplicationService>();
        if (action == "create")
        {
            var password = args.Get("password") ?? throw new ArgumentException("Use --password <value>.");
            var account = await administration.CreateAccountAsync(address, password, null, "cli", CancellationToken.None);
            Console.WriteLine($"Account created: {account.Address}");
            return 0;
        }
        var accountId = (await administration.GetAccountsAsync(CancellationToken.None))
            .SingleOrDefault(x => x.Address.Equals(address, StringComparison.OrdinalIgnoreCase))?.Id
            ?? throw new KeyNotFoundException("Account not found.");
        switch (action)
        {
            case "password":
                await administration.SetAccountPasswordAsync(
                    accountId,
                    args.Get("password") ?? throw new ArgumentException("Use --password <value>."),
                    "cli",
                    CancellationToken.None);
                break;
            case "enable":
                await administration.UpdateAccountAsync(accountId, true, null, "cli", CancellationToken.None);
                break;
            case "disable":
                await administration.UpdateAccountAsync(accountId, false, null, "cli", CancellationToken.None);
                break;
            default:
                return Fail("Usage: mailserver account create|password|enable|disable <address>");
        }
        Console.WriteLine($"Account updated: {address}");
        return 0;
    }

    private static async Task<int> RunHostAsync(CliArguments args)
    {
        var host = args.Positionals.ElementAtOrDefault(1)?.ToLowerInvariant() ?? "all";
        var configPath = GetExistingConfigPath(args);
        Environment.SetEnvironmentVariable(PortableConfiguration.EnvironmentVariable, configPath);
        string assemblyName;
        switch (host)
        {
            case "all":
                Environment.SetEnvironmentVariable("RunAll__Enabled", "true");
                assemblyName = "MailServer.Api";
                break;
            case "api":
                Environment.SetEnvironmentVariable("RunAll__Enabled", "false");
                assemblyName = "MailServer.Api";
                break;
            case "smtp":
                assemblyName = "MailServer.Smtp";
                break;
            case "imap":
                assemblyName = "MailServer.Imap";
                break;
            case "worker":
                assemblyName = "MailServer.Worker";
                break;
            default:
                return Fail("Usage: mailserver run all|api|smtp|imap|worker");
        }

        var assembly = Assembly.Load(assemblyName);
        var entryPoint = assembly.EntryPoint ?? throw new InvalidOperationException($"{assemblyName} has no entry point.");
        object? invocation;
        try
        {
            invocation = entryPoint.GetParameters().Length == 0
                ? entryPoint.Invoke(null, null)
                : entryPoint.Invoke(null, [Array.Empty<string>()]);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
        if (invocation is Task task) await task;
        return 0;
    }

    private static ServiceProvider BuildServices(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddSimpleConsole(options => options.SingleLine = true));
        services.AddMailServerInfrastructure(configuration);
        return services.BuildServiceProvider();
    }

    private static async Task ApplyMigrationsAsync(IServiceProvider services)
    {
        var factory = services.GetRequiredService<IDbContextFactory<MailDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
    }

    private static IConfigurationRoot LoadConfiguration(string path)
    {
        Directory.SetCurrentDirectory(Path.GetDirectoryName(path)!);
        return new ConfigurationBuilder()
            .SetBasePath(Path.GetDirectoryName(path)!)
            .AddJsonFile(path, optional: false)
            .AddEnvironmentVariables()
            .Build();
    }

    private static IEnumerable<string> ValidateConfiguration(IConfiguration configuration)
    {
        if (!Enum.TryParse<DatabaseProvider>(configuration["Database:Provider"], true, out _))
            yield return "Database:Provider must be Sqlite, PostgreSql, or MySql.";
        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("MailDatabase")))
            yield return "ConnectionStrings:MailDatabase is required.";
        var admin = configuration.GetSection(AdminServerOptions.SectionName).Get<AdminServerOptions>()
            ?? new AdminServerOptions();
        if (!admin.Http.Enabled && !admin.Https.Enabled)
            yield return "At least one AdminServer endpoint must be enabled.";
        if (!IPAddress.TryParse(admin.BindAddress, out _))
            yield return "AdminServer:BindAddress must be an IP address.";
        if (admin.Http.Enabled && !IsPort(admin.Http.Port)) yield return "Admin HTTP port is invalid.";
        if (admin.Https.Enabled && !IsPort(admin.Https.Port)) yield return "Admin HTTPS port is invalid.";
        var certificates = configuration.GetSection(CertificatesOptions.SectionName).Get<CertificatesOptions>()
            ?? new CertificatesOptions();
        if (admin.Https.Enabled && !certificates.Admin.IsConfigured)
            yield return "Admin HTTPS requires Certificates:Admin PEM paths.";
        if (string.IsNullOrWhiteSpace(configuration["Jwt:SigningKey"])
            || configuration["Jwt:SigningKey"]!.Length < 32)
            yield return "Jwt:SigningKey must contain at least 32 characters.";
        foreach (var pair in new[] { ("Mail", certificates.Mail), ("Admin", certificates.Admin) })
        {
            if (string.IsNullOrWhiteSpace(pair.Item2.CertificatePath)
                != string.IsNullOrWhiteSpace(pair.Item2.PrivateKeyPath))
                yield return $"Certificates:{pair.Item1} requires both certificate and private-key paths.";
        }
        foreach (var endpoint in configuration.GetSection("Smtp:Endpoints").Get<List<EndpointProbe>>() ?? [])
        {
            if (!IPAddress.TryParse(endpoint.Address, out _) || !IsPort(endpoint.Port))
                yield return $"Invalid SMTP endpoint {endpoint.Address}:{endpoint.Port}.";
        }
        foreach (var endpoint in configuration.GetSection("Imap:Endpoints").Get<List<EndpointProbe>>() ?? [])
        {
            if (!IPAddress.TryParse(endpoint.Address, out _) || !IsPort(endpoint.Port))
                yield return $"Invalid IMAP endpoint {endpoint.Address}:{endpoint.Port}.";
        }
    }

    private static void CheckCertificate(PemCertificateOptions options, string purpose)
    {
        using var certificate = PemCertificateLoader.LoadOptional(options, purpose);
        Console.WriteLine(certificate is null
            ? $"[INFO] {purpose} certificate is not configured."
            : $"[OK] {purpose} certificate: {certificate.Certificate.Subject}, expires {certificate.Certificate.NotAfter:O}");
    }

    private static void CheckPort(string address, int port, string name)
    {
        if (!IPAddress.TryParse(address, out var ip))
        {
            Console.WriteLine($"[FAIL] {name}: invalid bind address {address}.");
            return;
        }
        try
        {
            var listener = new TcpListener(ip, port);
            listener.Start();
            listener.Stop();
            Console.WriteLine($"[OK] {name}: {address}:{port} is available.");
        }
        catch (SocketException)
        {
            Console.WriteLine($"[FAIL] {name}: {address}:{port} is already in use or unavailable.");
        }
    }

    private static Dictionary<string, object?> CreateConfiguration(
        DatabaseProvider provider,
        string connection,
        string bind,
        int httpPort,
        int httpsPort,
        string certificatePath,
        string privateKeyPath,
        string mailCertificatePath,
        string mailPrivateKeyPath,
        string jwtKey) =>
        new()
        {
            ["Database"] = new { Provider = provider },
            ["ConnectionStrings"] = new { MailDatabase = connection },
            ["Storage"] = new { RootPath = "data/messages", MaximumMessageBytes = 52_428_800 },
            ["Plugins"] = new { Path = "plugins" },
            ["Certificates"] = new
            {
                Mail = new
                {
                    CertificatePath = mailCertificatePath,
                    PrivateKeyPath = mailPrivateKeyPath,
                    PrivateKeyPassword = ""
                },
                Admin = new { CertificatePath = certificatePath, PrivateKeyPath = privateKeyPath, PrivateKeyPassword = "" }
            },
            ["AdminServer"] = new
            {
                BindAddress = bind,
                Http = new { Enabled = httpPort > 0, Port = httpPort == 0 ? 8080 : httpPort },
                Https = new { Enabled = httpsPort > 0, Port = httpsPort == 0 ? 8443 : httpsPort, RedirectHttp = true }
            },
            ["Jwt"] = new { Issuer = "mailserver", Audience = "mailserver-admin", SigningKey = jwtKey, Minutes = 15 },
            ["Smtp"] = new
            {
                HostName = "mail.localhost",
                CommandTimeoutSeconds = 300,
                MaximumConnections = 200,
                MaximumRecipients = 100,
                MaximumMessageBytes = 52_428_800,
                Endpoints = new object[]
                {
                    new { Address = "0.0.0.0", Port = 25, ImplicitTls = false, RequireAuthentication = false },
                    new { Address = "0.0.0.0", Port = 465, ImplicitTls = true, RequireAuthentication = true },
                    new { Address = "0.0.0.0", Port = 587, ImplicitTls = false, RequireAuthentication = true }
                }
            },
            ["Imap"] = new
            {
                HostName = "mail.localhost",
                CommandTimeoutSeconds = 1800,
                MaximumConnections = 200,
                MaximumAppendBytes = 52_428_800,
                Endpoints = new object[]
                {
                    new { Address = "0.0.0.0", Port = 143, ImplicitTls = false },
                    new { Address = "0.0.0.0", Port = 993, ImplicitTls = true }
                }
            },
            ["Relay"] = new { Host = "localhost", Port = 25, UseStartTls = true, UserName = "", Password = "" },
            ["Delivery"] = new { BatchSize = 20, LeaseSeconds = 120, MaximumAttempts = 12, PollIntervalSeconds = 5 },
            ["Worker"] = new { HostName = "mail.localhost" }
        };

    private static string GetConfigPath(CliArguments args)
    {
        var path = args.Get("config")
            ?? Environment.GetEnvironmentVariable(PortableConfiguration.EnvironmentVariable)
            ?? Path.Combine(Environment.CurrentDirectory, "mailserver.json");
        return Path.GetFullPath(path);
    }

    private static string GetExistingConfigPath(CliArguments args)
    {
        var path = GetConfigPath(args);
        return File.Exists(path) ? path : throw new FileNotFoundException("Configuration file was not found.", path);
    }

    private static string Prompt(bool interactive, string label, string defaultValue)
    {
        if (!interactive) return defaultValue;
        Console.Write($"{label} [{defaultValue}]: ");
        var value = Console.ReadLine();
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
    }

    private static string DefaultConnection(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.Sqlite => "Data Source=data/mailserver.db",
        DatabaseProvider.PostgreSql => "Host=localhost;Port=5432;Database=mailserver;Username=mailserver;Password=mailserver",
        DatabaseProvider.MySql => "Server=localhost;Port=3306;Database=mailserver;User=mailserver;Password=mailserver",
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    private static string GenerateSecret(int bytes) => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(bytes));
    private static int ParsePort(string value, bool allowZero) =>
        int.TryParse(value, out var port) && (allowZero && port == 0 || IsPort(port))
            ? port
            : throw new FormatException($"Invalid port: {value}");
    private static bool IsPort(int port) => port is > 0 and <= 65535;

    private static void ProtectConfigurationFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return;
        }
        ProtectWindowsFile(path);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void ProtectWindowsFile(string path)
    {
        var identity = WindowsIdentity.GetCurrent();
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            identity.User!,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static void PrintHelp() =>
        Console.WriteLine(
            """
            MailServer portable CLI

              mailserver init [--provider Sqlite|PostgreSql|MySql] [--config path]
              mailserver doctor [--config path]
              mailserver config validate
              mailserver db status|update
              mailserver admin create|password <name> --password <value>
              mailserver domain add <domain> [--mode Relay|DirectMx]
              mailserver account create|password|enable|disable <address> [--password <value>]
              mailserver run all|api|smtp|imap|worker

            The default configuration is ./mailserver.json. Override it with --config or MAILSERVER_CONFIG.
            """);

    private sealed class EndpointProbe
    {
        public string Address { get; set; } = "0.0.0.0";
        public int Port { get; set; }
        public bool ImplicitTls { get; set; }
        public bool RequireAuthentication { get; set; }
    }
}

internal sealed class CliArguments
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Positionals { get; } = [];

    public string? Get(string name) => _options.GetValueOrDefault(name);
    public bool HasFlag(string name) => _options.ContainsKey(name);

    public static CliArguments Parse(string[] args)
    {
        var result = new CliArguments();
        for (var index = 0; index < args.Length; index++)
        {
            var value = args[index];
            if (!value.StartsWith("--", StringComparison.Ordinal))
            {
                result.Positionals.Add(value);
                continue;
            }
            var name = value[2..];
            if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                result._options[name] = args[++index];
            }
            else
            {
                result._options[name] = null;
            }
        }
        return result;
    }
}
