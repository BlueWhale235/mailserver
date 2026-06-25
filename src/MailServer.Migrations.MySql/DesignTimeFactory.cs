using MailServer.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MailServer.Migrations.MySql;

public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<MailDbContext>
{
    public MailDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("MAILSERVER_MYSQL_DESIGN")
            ?? "Server=localhost;Database=mailserver;User=mailserver;Password=mailserver";
        var options = new DbContextOptionsBuilder<MailDbContext>()
            .UseMySQL(connection, x => x.MigrationsAssembly(typeof(DesignTimeFactory).Assembly.FullName))
            .Options;
        return new MailDbContext(options);
    }
}
