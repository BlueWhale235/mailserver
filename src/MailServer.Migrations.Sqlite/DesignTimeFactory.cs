using MailServer.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MailServer.Migrations.Sqlite;

public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<MailDbContext>
{
    public MailDbContext CreateDbContext(string[] args)
    {
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());
        var options = new DbContextOptionsBuilder<MailDbContext>()
            .UseSqlite(
                "Data Source=mailserver-design.db",
                x => x.MigrationsAssembly(typeof(DesignTimeFactory).Assembly.FullName))
            .Options;
        return new MailDbContext(options);
    }
}
