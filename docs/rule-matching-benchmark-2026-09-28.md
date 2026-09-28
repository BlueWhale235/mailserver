# Rule matching benchmark (2026-09-28)

Command: `dotnet test tests/MailServer.Tests/MailServer.Tests.csproj --no-restore --filter FullyQualifiedName~MailRulePerformanceTests --logger "console;verbosity=detailed"`

Environment: Windows, .NET 10 Debug test build, SQLite. Each case warmed the rule snapshot, then evaluated 50 distinct mail facts inside separate evaluation scopes. Rules used exact sender-domain conditions, with one subject-based fallback rule. `early`, `late`, and `miss` refer to the matching rule position. The SQL counter and allocations cover the 50 warm-cache evaluations, not setup or snapshot loading. Allocation values use process-wide GC counters and can include test-runner noise. Snapshot bytes are estimates.

| Rules | Snapshot bytes | Early P95 ms / allocated bytes | Late P95 ms / allocated bytes | Miss P95 ms / allocated bytes | Warm SQL |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 10 | 7,460 | 0.017 / 188,600 | 0.030 / 352,584 | 0.016 / 327,968 | 0 |
| 100 | 94,330 | 0.006 / 213,200 | 0.009 / 237,672 | 0.010 / 204,840 | 0 |
| 1,000 | 942,126 | 0.013 / 221,400 | 0.009 / 237,712 | 0.007 / 204,944 | 0 |
| 10,000 | 9,474,122 | 0.022 / 213,200 | 0.125 / 246,000 | 0.016 / 188,600 | 0 |

These are single-run local measurements, not an improvement ratio or a memory-tier guarantee. The 10,000-rule snapshot requires a cache item budget above 9 MiB; smaller configured budgets use the existing bounded database-batch fallback. An optimization-before baseline, live MIME extraction counts, sustained throughput, MySQL, and Linux memory-limited load were not measured here.
