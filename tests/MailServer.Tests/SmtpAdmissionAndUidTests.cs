using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using MailServer.Domain;
using MailServer.Infrastructure;
using MailServer.Smtp;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MailServer.Tests;

public sealed class SmtpAdmissionAndUidTests
{
    [Fact]
    public async Task DataWaitsBefore354AndAcceptsAfterCapacityReturns()
    {
        await using var env = await SmtpFixture.CreateAsync(2000);
        using var occupied = env.Runtime.SmtpMessages.TryAcquire()!;
        await using var session = await env.ConnectAsync();
        await session.BeginDataAsync();
        var response = session.Reader.ReadLineAsync();
        await WaitUntilAsync(() => env.Runtime.SmtpMessages.Waiting == 1);
        Assert.False(response.IsCompleted);
        Assert.Equal(0, env.Inbound.Accepted);
        occupied.Dispose();
        Assert.StartsWith("354", await response.WaitAsync(TimeSpan.FromSeconds(3)));
        await session.Writer.WriteAsync("Subject: bounded\r\n\r\nhello\r\n.\r\n");
        Assert.StartsWith("250", await session.Reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)));
        await WaitUntilAsync(() => env.Runtime.SmtpMessages.Active == 0);
        Assert.Equal(1, env.Inbound.Accepted);
    }

    [Fact]
    public async Task WaitTimeoutReturns451WithoutReadingOrPersistingContent()
    {
        await using var env = await SmtpFixture.CreateAsync(100);
        using var occupied = env.Runtime.SmtpMessages.TryAcquire()!;
        await using var session = await env.ConnectAsync();
        await session.BeginDataAsync();
        Assert.StartsWith("451", await session.Reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, env.Inbound.Accepted);
        Assert.Equal(0, env.Runtime.SmtpMessages.Waiting);
        await session.Writer.WriteLineAsync("DATA");
        Assert.StartsWith("503", await session.Reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task QueueLimitRejectsExcessAndShutdownCancelsWaitWithoutPermitLeak()
    {
        await using var env = await SmtpFixture.CreateAsync(2000,1);
        using var occupied = env.Runtime.SmtpMessages.TryAcquire()!;
        await using var first = await env.ConnectAsync();
        await first.BeginDataAsync();
        var pending = first.Reader.ReadLineAsync();
        await WaitUntilAsync(() => env.Runtime.SmtpMessages.Waiting == 1);
        await using var second = await env.ConnectAsync();
        await second.BeginDataAsync();
        Assert.StartsWith("451", await second.Reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(1)));
        await env.Server.StopAsync(CancellationToken.None);
        await WaitUntilAsync(() => env.Runtime.SmtpMessages.Waiting == 0);
        occupied.Dispose();
        Assert.Equal(0,env.Runtime.SmtpMessages.Active);
        Assert.Equal(0,env.Inbound.Accepted);
    }

    [Theory]
    [InlineData("Sqlite")]
    [InlineData("MySql")]
    public async Task LegacyUidRepairUsesIndexedLastUidIncludingDeletedHistoryAndNewMailboxSkipsQuery(string provider)
    {
        var connection = Environment.GetEnvironmentVariable("MAILSERVER_TEST_MYSQL_CONNECTION");
        if (provider == "MySql" && string.IsNullOrWhiteSpace(connection)) return;
        if (provider == "MySql" && !connection!.Contains("mailserver_acceptance", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("An isolated acceptance database is required.");
        var capture = new Capture();
        var options = new DbContextOptionsBuilder<MailDbContext>().AddInterceptors(capture);
        if (provider == "MySql")
            options.UseMySQL(connection!, mysql => mysql.MigrationsAssembly("MailServer.Migrations.MySql"));
        else
        {
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());
            options.UseSqlite("Data Source=:memory:");
        }
        await using var db = new MailDbContext(options.Options, TestLocalLogs.Create());
        await db.Database.OpenConnectionAsync();
        if (provider == "MySql") await db.Database.MigrateAsync();
        else await db.Database.EnsureCreatedAsync();
        var domain = new MailDomain { Name = MailServer.Domain.SnowflakeIds.NewId().ToString() + ".uid.test" };
        var account = new MailAccount { Domain = domain, LocalPart = "inbox", PasswordHash = "unused" };
        var mailbox = new Mailbox { Account = account, Name = "INBOX", UidNext = 1 };
        var message = new StoredMessage { ContentKey = "shared", Size = 1, EnvelopeFrom = "sender@test.invalid" };
        db.Mailboxes.Add(mailbox);
        capture.Sql.Clear();
        await MailboxWriteAccounting.EnsureUidNextAsync(db, mailbox, default);
        Assert.Empty(capture.Sql);
        for (var uid = 1; uid <= 1000; uid++)
            db.MailboxMessages.Add(new MailboxMessage { Mailbox = mailbox, Message = message, Uid = uid,
                DeletedAt = uid > 500 ? DateTimeOffset.UtcNow : null });
        await db.SaveChangesAsync();
        capture.Sql.Clear();
        await MailboxWriteAccounting.EnsureUidNextAsync(db, mailbox, default);
        Assert.Equal(1001,mailbox.UidNext);
        var sql = Assert.Single(capture.Sql);
        Assert.Contains("ORDER BY",sql);
        Assert.Contains("DESC",sql);
        Assert.Contains("LIMIT",sql);
        Assert.DoesNotContain("MAX(",sql,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DeletedAt",sql);
        await using var command=db.Database.GetDbConnection().CreateCommand();
        command.CommandText=(provider == "MySql" ? "EXPLAIN " : "EXPLAIN QUERY PLAN ")
            + "SELECT Uid FROM mailbox_messages WHERE MailboxId = '"+mailbox.Id+"' ORDER BY Uid DESC LIMIT 1";
        await using var reader=await command.ExecuteReaderAsync();
        var plans=new List<string>();
        while(await reader.ReadAsync())
        {
            if (provider == "MySql")
            {
                Assert.Equal("IX_mailbox_messages_MailboxId_Uid", reader.GetString(reader.GetOrdinal("key")));
                var extra = reader.GetString(reader.GetOrdinal("Extra"));
                Assert.DoesNotContain("filesort", extra, StringComparison.OrdinalIgnoreCase);
            }
            else plans.Add(reader.GetString(3));
        }
        if (provider == "Sqlite")
        {
            Assert.Contains(plans,p=>p.Contains("IX_mailbox_messages_MailboxId_Uid"));
            Assert.DoesNotContain(plans,p=>p.Contains("TEMP B-TREE"));
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while(!predicate())await Task.Delay(10,timeout.Token);
    }
    private sealed class Capture : DbCommandInterceptor
    {
        internal readonly List<string> Sql=[];
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand c,CommandExecutedEventData e,DbDataReader r,CancellationToken t=default)
        { Sql.Add(c.CommandText);return ValueTask.FromResult(r); }
    }
    private sealed class FakeInbound : IInboundMessageService
    {
        internal int Accepted;
        public Task<bool> IsLocalRecipientAsync(string address,CancellationToken token)=>Task.FromResult(true);
        public async Task<InboundAcceptance> AcceptAsync(InboundEnvelope envelope,Stream content,CancellationToken token)
        { using var buffer=new MemoryStream();await content.CopyToAsync(buffer,token);Interlocked.Increment(ref Accepted);return new(MailServer.Domain.SnowflakeIds.NewId(),"test",1,0); }
    }
    private sealed class Session(TcpClient client,StreamReader reader,StreamWriter writer):IAsyncDisposable
    {
        internal StreamReader Reader=reader;
        internal StreamWriter Writer=writer;
        internal async Task BeginDataAsync()
        {
            foreach(var command in new[]{"HELO test.invalid","MAIL FROM:<sender@test.invalid>","RCPT TO:<inbox@test.invalid>"})
            { await Writer.WriteLineAsync(command);Assert.StartsWith("250",await Reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2))); }
            await Writer.WriteLineAsync("DATA");
        }
        public ValueTask DisposeAsync(){Writer.Dispose();Reader.Dispose();client.Dispose();return ValueTask.CompletedTask;}
    }
    private sealed class SmtpFixture : IAsyncDisposable
    {
        internal ResourceRuntime Runtime=null!;
        internal FakeInbound Inbound=new();
        internal SmtpServerService Server=null!;
        private ServiceProvider _provider=null!;
        private int _port;
        internal static async Task<SmtpFixture> CreateAsync(int wait,int queued=128)
        {
            var env=new SmtpFixture();
            env.Runtime=new ResourceRuntime("smtp",new ConfigurationBuilder().Build());
            env.Runtime.SmtpMessages.Resize(1);
            env._provider=new ServiceCollection().AddSingleton<IInboundMessageService>(env.Inbound).BuildServiceProvider();
            env.Server=new SmtpServerService(env._provider.GetRequiredService<IServiceScopeFactory>(),Options.Create(new SmtpServerOptions
            { ActiveMessageWaitMilliseconds=wait,MaximumWaitingMessages=queued,Endpoints=[new SmtpEndpointOptions { Address="127.0.0.1",Port=0,Security=ConnectionSecurity.Plain }] }),
                null!,NullLogger<SmtpServerService>.Instance,resources:env.Runtime);
            await env.Server.StartAsync(default);
            var listeners=(List<TcpListener>)typeof(SmtpServerService).GetField("_listeners",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(env.Server)!;
            await WaitUntilAsync(() => listeners.Count == 1);
            env._port=((IPEndPoint)listeners.Single().LocalEndpoint).Port;
            return env;
        }
        internal async Task<Session> ConnectAsync()
        {
            var client=new TcpClient();await client.ConnectAsync(IPAddress.Loopback,_port);
            var reader=new StreamReader(client.GetStream());
            var writer=new StreamWriter(client.GetStream()){AutoFlush=true,NewLine="\r\n"};
            Assert.StartsWith("220",await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)));
            return new(client,reader,writer);
        }
        public async ValueTask DisposeAsync(){await Server.StopAsync(default);Server.Dispose();await _provider.DisposeAsync();}
    }
}
