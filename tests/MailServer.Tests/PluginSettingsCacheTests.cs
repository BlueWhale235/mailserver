using System.Data.Common;
using System.Text.Json;
using MailServer.Domain;
using MailServer.Infrastructure;
using MailServer.PluginSdk;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
namespace MailServer.Tests;
public sealed class PluginSettingsCacheTests
{
    [Fact] public async Task MissingOrderAndSettingsAreCachedAndInvalidSaveCannotMutateThem()
    {
        await using var f = new Fixture(); await f.Initialize(); using var cache = Cache();
        var service = Service(f,cache);
        await service.GetOrderedManifestsAsync(default); await service.GetAsync("one",default);
        var reads = f.Counter.Reads;
        for(var i=0;i<100;i++) { await service.GetOrderedManifestsAsync(default); await service.GetAsync("one",default); }
        Assert.Equal(reads,f.Counter.Reads);
        await Assert.ThrowsAsync<FormatException>(()=>service.SaveAsync("one",false,new Dictionary<string,JsonElement> { ["limit"]=JsonSerializer.SerializeToElement(-1) },new HashSet<string>(),default));
        Assert.True((await service.GetAsync("one",default)).Enabled);
    }
    [Fact] public async Task CommittedOrderAndSettingsInvalidateSameProcessImmediately()
    {
        await using var f = new Fixture(); await f.Initialize(); using var cache = Cache(); var service = Service(f,cache);
        Assert.Equal("one",(await service.GetOrderedManifestsAsync(default))[0].Id);
        await service.SaveOrderAsync(["two","one"],default);
        Assert.Equal("two",(await service.GetOrderedManifestsAsync(default))[0].Id);
        var saved=await service.SaveAsync("one",false,new Dictionary<string,JsonElement> { ["limit"]=JsonSerializer.SerializeToElement(9) },new HashSet<string>(),default);
        Assert.False(saved.Plugin.Enabled); Assert.False((await service.GetAsync("one",default)).Enabled);
        Assert.Equal(9,(await service.GetAsync("one",default)).GetInteger("limit"));
        await using var db=await f.Factory.CreateDbContextAsync();
        Assert.Equal(2,await db.ConfigurationVersions.Where(x=>x.Scope==ConfigurationScopes.Settings).Select(x=>x.Version).SingleAsync());
    }
    [Fact] public async Task PartialAdminSavePreservesUnobservedExternalFieldsAndSecret()
    {
        await using var f=new Fixture(); await f.Initialize(); using var cache=Cache(); var service=Service(f,cache);
        await service.GetAsync("one",default);
        var encrypted=f.Protection.CreateProtector("MailServer.PluginSettings.v1").Protect("externally-changed-secret");
        await using(var db=await f.Factory.CreateDbContextAsync())
        {
            db.SystemSettings.Add(new SystemSetting {Key="plugin:one:settings",Value=JsonSerializer.Serialize(new {Enabled=true,Values=new Dictionary<string,JsonElement> { ["template"]=JsonSerializer.SerializeToElement("externally-changed-template"),["limit"]=JsonSerializer.SerializeToElement(6) },Secrets=new Dictionary<string,string> { ["password"]=encrypted }})});
            await db.SaveChangesAsync();
        }
        await service.SaveAsync("one",true,new Dictionary<string,JsonElement> { ["limit"]=JsonSerializer.SerializeToElement(9) },new HashSet<string>(),default);
        var saved=await service.GetAsync("one",default);
        Assert.Equal("externally-changed-template",saved.Values["template"].GetString());
        Assert.Equal("externally-changed-secret",saved.Values["password"].GetString()); Assert.Equal(9,saved.GetInteger("limit"));
    }
    [Fact] public async Task OtherProcessVersionObservationRefreshesMissingOrder()
    {
        await using var f = new Fixture(); await f.Initialize(); using var readerCache=Cache(); using var writerCache=Cache();
        var reader=Service(f,readerCache);var writer=Service(f,writerCache);
        Assert.Equal("one",(await reader.GetOrderedManifestsAsync(default))[0].Id);
        await writer.SaveOrderAsync(["two","one"],default);
        Assert.Equal("one",(await reader.GetOrderedManifestsAsync(default))[0].Id);
        readerCache.ObserveVersions(new ConfigurationVersionsSnapshot(0,0,writerCache.Versions.Settings));
        Assert.Equal("two",(await reader.GetOrderedManifestsAsync(default))[0].Id);
    }
    [Fact] public async Task AbsoluteTtlRefreshesWhenVersionNotificationIsLost()
    {
        await using var f = new Fixture(); await f.Initialize(); using var readerCache=Cache(1);var reader=Service(f,readerCache);
        await reader.GetOrderedManifestsAsync(default);
        await Service(f,null).SaveOrderAsync(["two","one"],default);
        await Task.Delay(1100);
        Assert.Equal("two",(await reader.GetOrderedManifestsAsync(default))[0].Id);
    }
    [Fact] public async Task OversizedEntryIsNotRetainedAndMalformedOrderCachesFallback()
    {
        await using var f = new Fixture(); await f.Initialize();
        await using(var db=await f.Factory.CreateDbContextAsync()){db.SystemSettings.Add(new SystemSetting { Key="plugin:order",Value="invalid-json"});await db.SaveChangesAsync();}
        using(var cache=Cache()){var service=Service(f,cache);await service.GetOrderedManifestsAsync(default);var reads=f.Counter.Reads;await service.GetOrderedManifestsAsync(default);Assert.Equal(reads,f.Counter.Reads);}
        using(var small=Cache()){small.Resize(128,128);var service=Service(f,small);await service.GetOrderedManifestsAsync(default);var reads=f.Counter.Reads;await service.GetOrderedManifestsAsync(default);Assert.True(f.Counter.Reads>reads);Assert.Equal(0,small.EstimatedBytes);}
    }
    private static ConfigurationSnapshotCache Cache(int ttl=60)=>new(Options.Create(new ConfigurationCacheOptions { Enabled=true,SizeLimitBytes=1024*1024,MaximumEntryBytes=1024*64,AbsoluteRefreshSeconds=ttl }),NullLogger<ConfigurationSnapshotCache>.Instance);
    private static PluginSettingsService Service(Fixture f,ConfigurationSnapshotCache? cache)
    {
        var catalog=new PluginCatalog();
        foreach(var (id,order) in new[]{("one",10),("two",20)})catalog.Add(new LoadedPlugin(new PluginManifest { Id=id,Version="1",ContractVersion=PluginContract.Version,EntryAssembly="unused.dll",Order=order,DefaultEnabled=true,
            Settings=[new PluginSettingDefinition { Key="limit",Label="Limit",Type=PluginSettingType.Integer,Minimum=1,Maximum=10,DefaultValue=JsonSerializer.SerializeToElement(5) },new PluginSettingDefinition {Key="template",Label="Template",Type=PluginSettingType.Text,DefaultValue=JsonSerializer.SerializeToElement("default")},new PluginSettingDefinition {Key="password",Label="Password",Type=PluginSettingType.Password}] },null,false));
        return new(f.Factory,catalog,f.Protection,cache);
    }
    private sealed class Counter : DbCommandInterceptor
    {
        public int Reads;
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,CommandExecutedEventData data,DbDataReader result,CancellationToken cancellationToken=default){Interlocked.Increment(ref Reads);return ValueTask.FromResult(result);}
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _path=Path.Combine(Path.GetTempPath(),$"plugin-cache-{Guid.NewGuid():N}.db");
        public readonly Counter Counter=new(); public readonly IDataProtectionProvider Protection=new EphemeralDataProtectionProvider(); public IDbContextFactory<MailDbContext> Factory { get; }
        public Fixture(){SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());Factory=new PooledDbContextFactory<MailDbContext>(new DbContextOptionsBuilder<MailDbContext>().UseSqlite($"Data Source={_path};Pooling=False").AddInterceptors(Counter).Options);}
        public async Task Initialize(){await using var db=await Factory.CreateDbContextAsync();await db.Database.EnsureCreatedAsync();}
        public ValueTask DisposeAsync(){File.Delete(_path);return ValueTask.CompletedTask;}
    }
}
