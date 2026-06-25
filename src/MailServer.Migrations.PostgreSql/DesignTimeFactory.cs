using MailServer.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MailServer.Migrations.PostgreSql;

public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<MailDbContext>
{
    public MailDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("MAILSERVER_POSTGRES_DESIGN")
            ?? "Host=localhost;Database=mailserver;Username=mailserver;Password=mailserver";
        var options = new DbContextOptionsBuilder<MailDbContext>()
            .UseNpgsql(connection, x => x.MigrationsAssembly(typeof(DesignTimeFactory).Assembly.FullName))
            .Options;
        return new MailDbContext(options);
    }
}
