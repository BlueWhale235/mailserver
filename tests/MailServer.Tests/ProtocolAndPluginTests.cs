using System.Text;
using MailServer.Imap;
using MailServer.Infrastructure;
using MailServer.PluginSdk;
using MailServer.SamplePlugin;
using MailServer.Smtp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MailServer.Tests;

public sealed class ProtocolAndPluginTests
{
    [Fact]
    public async Task SmtpDataReaderRemovesDotStuffingWithoutBufferingMessageInMemory()
    {
        await using var input = new MemoryStream(Encoding.ASCII.GetBytes("Subject: test\r\n\r\n..leading-dot\r\n.\r\n"));
        await using var connection = new SmtpConnection(input, TimeSpan.FromSeconds(1), 4096);

        await using var message = await connection.ReadDataAsync(CancellationToken.None);
        using var reader = new StreamReader(message);

        Assert.Equal("Subject: test\r\n\r\n.leading-dot\r\n", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task SmtpDataReaderRejectsOversizedMessagesDuringIngestion()
    {
        await using var input = new MemoryStream(Encoding.ASCII.GetBytes("1234567890\r\n.\r\n"));
        await using var connection = new SmtpConnection(input, TimeSpan.FromSeconds(1), 5);

        await Assert.ThrowsAsync<InvalidDataException>(() => connection.ReadDataAsync(CancellationToken.None));
    }

    [Fact]
    public void ImapTokenizerPreservesQuotedSpacesAndParenthesizedFlagLists()
    {
        var tokens = ImapTokenizer.Tokenize("\"Archive 2026\" (\\Seen \\Flagged)");

        Assert.Equal(["Archive 2026", "(\\Seen \\Flagged)"], tokens);
    }

    [Fact]
    public void PluginLoaderDiscoversTrustedDllAndRegistersMiddleware()
    {
        var root = Path.GetDirectoryName(typeof(LargeMessagePlugin).Assembly.Location)!;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Plugins:Path"] = root })
            .Build();
        var services = new ServiceCollection();

        services.AddMailServerPlugins(configuration, "smtp");
        using var provider = services.BuildServiceProvider();

        Assert.Single(provider.GetServices<IMessageAcceptanceMiddleware>());
        Assert.Single(provider.GetRequiredService<PluginCatalog>().Plugins);
    }
}
