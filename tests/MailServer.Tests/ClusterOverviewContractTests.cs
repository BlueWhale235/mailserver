using System.Text.Json;
using MailServer.Infrastructure;
using Xunit;

namespace MailServer.Tests;

public sealed class ClusterOverviewContractTests
{
    [Fact]
    public void OverviewConfigurationExposesOnlyAdministrativeMetadata()
    {
        Assert.Equal(typeof(ClusterConfigurationView), typeof(ClusterOverview).GetProperty("Configuration")!.PropertyType);
        var configuration = new ClusterConfigurationView(Guid.NewGuid(), "cluster", "primary", DateTimeOffset.UtcNow);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(configuration, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(new[] { "createdAt", "id", "name", "primaryNodeId" },
            json.RootElement.EnumerateObject().Select(x => x.Name).OrderBy(x => x).ToArray());
    }
}
