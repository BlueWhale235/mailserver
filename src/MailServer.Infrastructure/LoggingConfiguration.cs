using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Configuration;

namespace MailServer.Infrastructure;

public static class LoggingConfiguration
{
    public static ILoggingBuilder AddMailServerLogging(
        this ILoggingBuilder logging,
        IConfiguration configuration)
    {
        var options = configuration.GetSection(MailServerLoggingOptions.SectionName)
            .Get<MailServerLoggingOptions>() ?? new MailServerLoggingOptions();

        logging.ClearProviders();
        logging.AddConfiguration(configuration.GetSection("Logging"));
        logging.Services.AddSingleton(options);

        if (options.Console.Enabled || !options.File.Enabled)
        {
            logging.Services.AddSingleton<ILoggerProvider>(_ => new MailServerConsoleLoggerProvider(options));
        }

        if (options.File.Enabled)
        {
            logging.Services.AddSingleton<ILoggerProvider>(_ => new MailServerFileLoggerProvider(options));
        }

        return logging.AddMailServerLogFilters();
    }

    public static ILoggingBuilder AddMailServerLogFilters(this ILoggingBuilder logging)
    {
        logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
        logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
        return logging;
    }
}

public static class MailServerLogFormatter
{
    public static string Format(
        string categoryName,
        LogLevel logLevel,
        string message,
        Exception? exception,
        DateTimeOffset timestamp,
        string timestampFormat,
        bool color)
    {
        var time = timestamp.ToLocalTime().ToString(
            string.IsNullOrWhiteSpace(timestampFormat) ? "yyyy/MM/dd HH:mm:ss" : timestampFormat,
            CultureInfo.InvariantCulture);
        var level = ShortLevel(logLevel);
        var protocol = ProtocolLabel(categoryName);
        var category = protocol is null
            ? categoryName
            : $"{protocol} {categoryName}";

        if (color)
        {
            level = Colorize(level, LevelColor(logLevel));
            if (protocol is not null)
            {
                category = $"{Colorize(protocol, ProtocolColor(protocol))} {categoryName}";
            }
        }

        var line = $"{time} [{level}] {category}: {message}";
        return exception is null ? line : line + Environment.NewLine + exception;
    }

    public static string StripAnsi(string value)
    {
        StringBuilder? builder = null;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\u001b' && index + 1 < value.Length && value[index + 1] == '[')
            {
                builder ??= new StringBuilder(value[..index]);
                index += 2;
                while (index < value.Length && !char.IsAsciiLetter(value[index]))
                {
                    index++;
                }
                continue;
            }

            builder?.Append(value[index]);
        }

        return builder?.ToString() ?? value;
    }

    private static string ShortLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRITICAL",
        _ => level.ToString().ToUpperInvariant()
    };

    private static string? ProtocolLabel(string categoryName)
    {
        if (categoryName.Contains(".Smtp", StringComparison.OrdinalIgnoreCase)) return "SMTP";
        if (categoryName.Contains(".Imap", StringComparison.OrdinalIgnoreCase)) return "IMAP";
        if (categoryName.Contains(".Worker", StringComparison.OrdinalIgnoreCase)) return "WORKER";
        if (categoryName.Contains(".Api", StringComparison.OrdinalIgnoreCase)) return "API";
        return null;
    }

    private static string LevelColor(LogLevel level) => level switch
    {
        LogLevel.Trace => "90",
        LogLevel.Debug => "36",
        LogLevel.Information => "32",
        LogLevel.Warning => "33",
        LogLevel.Error => "31",
        LogLevel.Critical => "35",
        _ => "0"
    };

    private static string ProtocolColor(string protocol) => protocol switch
    {
        "SMTP" => "36",
        "IMAP" => "35",
        "WORKER" => "33",
        "API" => "34",
        _ => "0"
    };

    private static string Colorize(string value, string color) => $"\u001b[{color}m{value}\u001b[0m";
}

internal sealed class MailServerConsoleLoggerProvider(MailServerLoggingOptions options) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new ConsoleLogger(categoryName, options);
    public void Dispose() { }

    private sealed class ConsoleLogger(string categoryName, MailServerLoggingOptions options) : ILogger
    {
        private static readonly object Sync = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            if (string.IsNullOrEmpty(message) && exception is null) return;

            var line = MailServerLogFormatter.Format(
                categoryName,
                logLevel,
                message,
                exception,
                DateTimeOffset.Now,
                options.TimestampFormat,
                options.Console.Color);
            lock (Sync)
            {
                System.Console.WriteLine(line);
            }
        }
    }
}

internal sealed class MailServerFileLoggerProvider : ILoggerProvider
{
    private readonly MailServerLoggingOptions _options;
    private readonly object _sync = new();
    private StreamWriter? _writer;
    private DateOnly _currentDate;
    private bool _fileUnavailable;

    public MailServerFileLoggerProvider(MailServerLoggingOptions options)
    {
        _options = options;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    public void Dispose()
    {
        lock (_sync)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void Write(string categoryName, LogLevel logLevel, string message, Exception? exception)
    {
        if (_fileUnavailable) return;
        var now = DateTimeOffset.Now;
        var line = MailServerLogFormatter.Format(
            categoryName,
            logLevel,
            message,
            exception,
            now,
            _options.TimestampFormat,
            color: false);
        line = MailServerLogFormatter.StripAnsi(line).Replace("\u001b", string.Empty, StringComparison.Ordinal);

        lock (_sync)
        {
            try
            {
                var today = DateOnly.FromDateTime(now.LocalDateTime);
                if (_writer is null || _currentDate != today)
                {
                    _writer?.Dispose();
                    System.IO.Directory.CreateDirectory(_options.File.Directory);
                    var path = Path.Combine(_options.File.Directory, $"{today:yyyy-MM-dd}.log");
                    _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    {
                        AutoFlush = true
                    };
                    _currentDate = today;
                }

                _writer.WriteLine(line);
            }
            catch (Exception exceptionToIgnore) when (exceptionToIgnore is IOException or UnauthorizedAccessException)
            {
                _fileUnavailable = true;
            }
        }
    }

    private sealed class FileLogger(string categoryName, MailServerFileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            if (string.IsNullOrEmpty(message) && exception is null) return;
            provider.Write(categoryName, logLevel, message, exception);
        }
    }
}

internal sealed class NullScope : IDisposable
{
    public static NullScope Instance { get; } = new();
    public void Dispose() { }
}
