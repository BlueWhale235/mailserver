namespace MailServer.Infrastructure;

public sealed record PerformanceSettings
{
    public bool Enabled { get; init; } = true;
    public double SampleRate { get; init; } = .01;
    public int ApiMilliseconds { get; init; } = 500;
    public int ImapMilliseconds { get; init; } = 500;
    public int SmtpMilliseconds { get; init; } = 1000;
    public int DeliveryMilliseconds { get; init; } = 3000;
    public int StageMilliseconds { get; init; } = 200;
    public int SqlMilliseconds { get; init; } = 200;
    public int SqlTotalMilliseconds { get; init; } = 500;
    public int RepeatedSqlCount { get; init; } = 20;
    public int DetailDays { get; init; } = 30;
    public int AggregateDays { get; init; } = 30;
    public long DetailBudgetBytes { get; init; } = 512L * 1024 * 1024;
    public DateTimeOffset? DiagnosticUntil { get; init; }

    public void Validate()
    {
        if (!double.IsFinite(SampleRate) || SampleRate is < 0 or > 1
            || new[] { ApiMilliseconds, ImapMilliseconds, SmtpMilliseconds, DeliveryMilliseconds,
                StageMilliseconds, SqlMilliseconds, SqlTotalMilliseconds }.Any(x => x is < 1 or > 3_600_000)
            || RepeatedSqlCount is < 2 or > 1000 || DetailDays is < 1 or > 90
            || AggregateDays is < 1 or > 365 || DetailBudgetBytes is < 1_048_576 or > 10_737_418_240L)
            throw new ArgumentException("性能采集配置超出允许范围。");
    }

    public int Threshold(string module) => module switch
    { "smtp" => SmtpMilliseconds, "worker" => DeliveryMilliseconds, "imap" => ImapMilliseconds, _ => ApiMilliseconds };
}

public sealed class PerformanceOperation
{
    public long Id { get; set; } = MailServer.Domain.SnowflakeIds.NewId();
    public string TraceId { get; set; } = "";
    public string? SourceTraceId { get; set; }
    public string ProcessId { get; set; } = "";
    public string Module { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "ok";
    public DateTimeOffset StartedAt { get; set; }
    public double DurationMilliseconds { get; set; }
    public double ProcessingMilliseconds { get; set; }
    public double SqlMilliseconds { get; set; }
    public int SqlCount { get; set; }
    public bool Slow { get; set; }
    public bool Truncated { get; set; }
    public long? MessageId { get; set; }
    public long? DeliveryJobId { get; set; }
    public int? Attempt { get; set; }
    public double? QueueWaitMilliseconds { get; set; }
    public double? RetryBackoffMilliseconds { get; set; }
    public bool ClockAnomaly { get; set; }
    public int DetailBytes { get; set; }
    public string DetailsJson { get; set; } = "[]";
}

public sealed record PerformanceStage(string Id, string? ParentId, string Name, string Category,
    double OffsetMilliseconds, double DurationMilliseconds, double SelfMilliseconds,
    string Status, string? ErrorType = null, string? SqlFingerprint = null,
    double? ExecutionMilliseconds = null, long? Rows = null);

public sealed class PerformanceMinute
{
    public long Id { get; set; } = MailServer.Domain.SnowflakeIds.NewId();
    public string ProcessId { get; set; } = "";
    public DateTimeOffset Minute { get; set; }
    public string Kind { get; set; } = "operation";
    public string Module { get; set; } = "";
    public string Name { get; set; } = "";
    public long Count { get; set; }
    public long Errors { get; set; }
    public long Slow { get; set; }
    public double TotalMilliseconds { get; set; }
    public double MaximumMilliseconds { get; set; }
    public string HistogramJson { get; set; } = "[]";
}

public sealed class PerformanceSql
{
    public string Fingerprint { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Template { get; set; } = "";
    public DateTimeOffset LastSeenAt { get; set; }
}

public sealed class PerformanceProcess
{
    public string ProcessId { get; set; } = "";
    public DateTimeOffset LastSeenAt { get; set; }
    public long DroppedOperations { get; set; }
    public long DroppedAggregates { get; set; }
    public long TruncatedOperations { get; set; }
    public long BufferBytes { get; set; }
    public long BudgetBytes { get; set; }
    public bool StorageUnavailable { get; set; }
}
