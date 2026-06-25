namespace MailServer.Infrastructure;

public sealed class AdminServerOptions
{
    public const string SectionName = "AdminServer";
    public string BindAddress { get; set; } = "127.0.0.1";
    public AdminHttpEndpointOptions Http { get; set; } = new() { Enabled = true, Port = 8080 };
    public AdminHttpsEndpointOptions Https { get; set; } = new();
}

public class AdminHttpEndpointOptions
{
    public bool Enabled { get; set; }
    public int Port { get; set; }
}

public sealed class AdminHttpsEndpointOptions : AdminHttpEndpointOptions
{
    public bool RedirectHttp { get; set; } = true;
}
