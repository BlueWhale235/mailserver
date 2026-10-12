using MailServer.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;
namespace MailServer.Tests;
public sealed class AdaptiveResourceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static AdaptiveResourceSample Sample(int seconds, long memory = 2L * 1024 * 1024 * 1024, bool valid = true,
        double database = 1, double cpu = 50, int inbound = 1, int outbound = 1, int heavy = 0,
        long completed = 0, double operation = 1, double gc = 0) => new(Start.AddSeconds(seconds), valid, memory,
            8L * 1024 * 1024 * 1024, cpu, gc, new(completed, completed, inbound, outbound, operation, database, 20, 256L * 1024 * 1024, operation, operation), inbound, outbound, heavy);
    [Fact] public void DefaultsDisableExperimentAndInvalidCapsRejected()
    {
        Assert.False(AdaptiveResourceOptions.Read(new ConfigurationBuilder().Build()).Enabled);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["AdaptiveResources:MaximumHeavy"] = "13" }).Build();
        Assert.Throws<ArgumentException>(() => AdaptiveResourceOptions.Read(configuration));
    }
    [Fact] public void FixedModeNeverExpandsAndMissingSampleRestoresInitial()
    {
        var p = new AdaptiveResourcePolicy(new() { Enabled = true });
        Assert.Equal("fixed", p.Evaluate(Sample(0),16,32,4).Reason);
        var d = p.Evaluate(Sample(1,valid:false),24,48,8);
        Assert.Equal((16,32,4,"sample-unavailable"),(d.Inbound,d.Outbound,d.Heavy,d.Reason));
    }
    [Fact] public void BaselineLearnsWarmMaximumBeforeFreezing()
    {
        var p = new AdaptiveResourcePolicy(new() { Enabled = true, AutomaticEnabled = true });
        Assert.Equal("capacity-guard",p.Evaluate(Sample(0,database:1),16,32,4).Reason);
        Assert.Equal("capacity-guard",p.Evaluate(Sample(5,database:10),16,32,4).Reason);
        Assert.Equal("increase-inbound",p.Evaluate(Sample(10,database:8),16,32,4).Reason);
        Assert.Equal("observe-increase",p.Evaluate(Sample(12,database:15),18,32,4).Reason);
    }
    [Fact] public void BothRolesExpandAlternatelyAfterObservation()
    {
        var p = new AdaptiveResourcePolicy(new() { Enabled = true, AutomaticEnabled = true, DatabaseBaselineWindowSeconds = 0 });
        var a = p.Evaluate(Sample(0),16,32,4);
        Assert.Equal((18,32),(a.Inbound,a.Outbound));
        Assert.Equal("observe-increase",p.Evaluate(Sample(2,completed:10),18,32,4).Reason);
        var b = p.Evaluate(Sample(6,completed:30),18,32,4);
        Assert.Equal((18,36),(b.Inbound,b.Outbound));
    }
    [Fact] public void RotatingDimensionsDoesNotLetHeavyDemandStarveOtherRoles()
    {
        var p = new AdaptiveResourcePolicy(new() { Enabled=true,AutomaticEnabled=true,DatabaseBaselineWindowSeconds=0 });
        var a=p.Evaluate(Sample(0,heavy:10),16,32,4); Assert.Equal("increase-inbound",a.Reason);
        var b=p.Evaluate(Sample(6,heavy:10,completed:100),a.Inbound,a.Outbound,a.Heavy); Assert.Equal("increase-outbound",b.Reason);
        var c=p.Evaluate(Sample(12,heavy:10,completed:200),b.Inbound,b.Outbound,b.Heavy); Assert.Equal("increase-heavy",c.Reason);
        Assert.Equal((18,36,5),(c.Inbound,c.Outbound,c.Heavy));
    }
    [Fact] public void HostSaturationOrPressurePreventsExpansionAndMissingMetricsRestoresDefaults()
    {
        foreach(var sample in new[] {Sample(0) with { HostCpuPercent=96 },Sample(0) with { HostCpuPressurePercent=21 }})
        {var p=new AdaptiveResourcePolicy(new() { Enabled=true,AutomaticEnabled=true,DatabaseBaselineWindowSeconds=0 });Assert.Equal("capacity-guard",p.Evaluate(sample,16,32,4).Reason);}
        var missing=new AdaptiveResourcePolicy(new() { Enabled=true,AutomaticEnabled=true,DatabaseBaselineWindowSeconds=0 }).Evaluate(Sample(0) with {HostMetricsValid=false},24,48,8);
        Assert.Equal((16,32,4,"sample-unavailable"),(missing.Inbound,missing.Outbound,missing.Heavy,missing.Reason));
    }
    [Fact] public void KnownMemoryEmergencyStillStopsWhenHostCpuMetricsAreMissing()
    {
        foreach(var automatic in new[] {false,true})
        {
            var p=new AdaptiveResourcePolicy(new() {Enabled=true,AutomaticEnabled=automatic,DatabaseBaselineWindowSeconds=0});
            Assert.False(p.Evaluate(Sample(0,memory:64L*1024*1024) with {HostMetricsValid=false},16,32,4).StopRequested);
            Assert.True(p.Evaluate(Sample(5,memory:64L*1024*1024) with {HostMetricsValid=false},16,32,4).StopRequested);
        }
    }
    [Fact] public void OtherRolesProgressCannotHideRegressionOfExpandedRole()
    {
        var p=new AdaptiveResourcePolicy(new() { Enabled=true,AutomaticEnabled=true,DatabaseBaselineWindowSeconds=0 });
        p.Evaluate(Sample(0,inbound:0,outbound:0),16,32,4);
        p.Evaluate(Sample(2,completed:20),16,32,4);
        var s=Sample(8,completed:21,operation:2);
        s=s with { Telemetry=s.Telemetry with {OutboundCompleted=200,OutboundP95Milliseconds=1} };
        Assert.Equal("revert-no-benefit",p.Evaluate(s,18,32,4).Reason);
    }
    [Fact] public void CpuGuardPreventsExpansionAndPressureDownscalesWithoutRevocation()
    {
        var p = new AdaptiveResourcePolicy(new() { Enabled = true, AutomaticEnabled = true, DatabaseBaselineWindowSeconds = 0 });
        Assert.Equal("capacity-guard",p.Evaluate(Sample(0,cpu:95),16,32,4).Reason);
        var d = p.Evaluate(Sample(2,memory:300L*1024*1024),16,32,4);
        Assert.Equal((12,24,3),(d.Inbound,d.Outbound,d.Heavy));
        Assert.Equal("downscale-cooldown",p.Evaluate(Sample(4,memory:300L*1024*1024),12,24,3).Reason);
    }
    [Fact] public void CriticalMemoryStopsFixedAndAutomaticModesAfterFiveSeconds()
    {
        foreach (var automatic in new[] { false,true })
        {
            var p = new AdaptiveResourcePolicy(new() { Enabled = true, AutomaticEnabled = automatic });
            Assert.False(p.Evaluate(Sample(0,memory:64L*1024*1024),16,32,4).StopRequested);
            Assert.True(p.Evaluate(Sample(5,memory:64L*1024*1024),16,32,4).StopRequested);
        }
    }
    [Fact] public void DeterioratingIncreaseRollsBackAndCoolsDown()
    {
        var p = new AdaptiveResourcePolicy(new() { Enabled = true, AutomaticEnabled = true, DatabaseBaselineWindowSeconds = 0 });
        p.Evaluate(Sample(0,inbound:0,outbound:0),16,32,4);
        Assert.Equal(18,p.Evaluate(Sample(2,completed:20),16,32,4).Inbound);
        var d = p.Evaluate(Sample(8,completed:21,operation:2),18,32,4);
        Assert.Equal((16,32,4,"revert-no-benefit"),(d.Inbound,d.Outbound,d.Heavy,d.Reason));
        Assert.Equal("hold",p.Evaluate(Sample(10,completed:25),16,32,4).Reason);
    }
    [Fact] public void TelemetryExpiresStaleLatencyButKeepsCompletionCounters()
    {
        var clock = new Clock(); var t = new AdaptiveResourceTelemetry(clock);
        t.RecordDatabase(TimeSpan.FromSeconds(2)); t.RecordCompletion(true,TimeSpan.FromSeconds(1));
        clock.Now = Start.AddSeconds(7);
        var s = t.Snapshot();
        Assert.Equal(0,s.DatabaseSamples); Assert.Equal(0,s.OperationP95Milliseconds); Assert.Equal(1,s.InboundCompleted);
    }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = Start; public override DateTimeOffset GetUtcNow() => Now; }
    [Fact] public void TelemetryKeepsTotalsAndResetsDemandWithBoundedPercentiles()
    {
        var telemetry = new AdaptiveResourceTelemetry();
        telemetry.RecordInboundDemand(); telemetry.RecordOutboundDemand();
        for(var i=0;i<600;i++) telemetry.RecordDatabase(TimeSpan.FromMilliseconds(i));
        telemetry.RecordCompletion(true,TimeSpan.FromMilliseconds(10));
        var a=telemetry.Snapshot(); var b=telemetry.Snapshot();
        Assert.Equal(512,a.DatabaseSamples); Assert.Equal(574,a.DatabaseP95Milliseconds);
        Assert.Equal(1,a.InboundDemand); Assert.Equal(0,b.InboundDemand); Assert.Equal(1,b.InboundCompleted); Assert.Equal(10,b.InboundP95Milliseconds); Assert.Equal(0,b.OutboundP95Milliseconds);
    }
}
