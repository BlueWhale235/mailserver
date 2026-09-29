# Test-server MySQL SMTP load test — 2026-09-29

## Environment

- Server: Ubuntu 24.04, Linux x64, about 887 MiB RAM and 2 GiB swap.
- Application: `cf7fa9c`, self-contained Linux x64 binary, SHA-256 `4cf2bbd7bf402f7e6133d15756a9e50f42e44dcdf22ccae7616d099665a14122`.
- Database: Azure MySQL, new empty schema migrated through all seven migrations.
- SMTP client and server ran on the same host over loopback port 25. Mail was sent only to the local `inbox@loadtest.invalid` account.
- Original SQLite instance, including its database, messages, configuration, security assets and logs, was preserved at `/www/mailserver-archive-20260929-pre-loadtest`. The new MySQL instance is at `/www/mailserver`.
- SMTP connection limits and database pooling settings were left at their defaults.

## Method

One 1 MiB message warmed the path. The final deployed binary was then measured with five sequential 1 MiB sends, sixteen 1 MiB sends through eight concurrent connections, and five sequential 10 MiB sends. Each message used a fresh SMTP connection; the duration includes connect, envelope and DATA acknowledgement. The DATA duration isolates the command and server acknowledgement. P95 is the nearest-rank observation from each small sample. Peak RSS was sampled from `/proc` during each run. These are short diagnostic runs, not a steady-state throughput benchmark.

| Scenario | Accepted / attempted | Median total | P95 total | Median DATA | P95 DATA | Peak RSS |
|---|---:|---:|---:|---:|---:|---:|
| 1 MiB, sequential | 5 / 5 | 276 ms | 445 ms | 126 ms | 178 ms | 228 MiB |
| 1 MiB, 8 concurrent | 5 / 16 | 591 ms | 1,069 ms | 333 ms | 671 ms | 279 MiB |
| 10 MiB, sequential | 5 / 5 | 2,727 ms | 4,664 ms | 2,666 ms | 4,609 ms | 394 MiB |

Concurrent duration percentiles cover only accepted messages. Eleven concurrent attempts returned SMTP 451. Server logs identify MySQL transaction deadlocks; the application correctly returned a temporary failure rather than accepting a message it could not commit. A separate retry probe completed all eight deliveries in each of its first two rounds, but rapid successive rounds exhausted the default per-source connection burst allowance and later attempts were rejected by connection protection. The test driver was not paced to this allowance, so those later rounds do not measure database recovery.

The performance table contained 21 sampled/slow successful SMTP DATA operations from both pre-fix and final-binary runs. Their mean SQL count was 10.4 and mean SQL time was 58 ms; these figures are diagnostic only and are not a matched before/after comparison. No allocation measurement or pool-disabled comparison was captured in this server run.

## Findings and validation

- A fresh MySQL installation initially failed at `domain_dns_settings` because several long `varchar` columns exceeded the row-size limit. The unindexed long fields were changed to MySQL `text`, and a clean migration then succeeded.
- The sent-copy cleanup worker initially generated a MySQL DELETE that read from the same target table in a subquery. Removing the soft-delete query filter from this cleanup query produces a direct DELETE. The error did not recur after the final binary started.
- The health endpoint returned HTTP 200 after deployment. Local SMTP smoke delivery succeeded. All 400 automated tests passed. The server remained running after the final test.
- The opt-in MySQL acceptance suite was not run against a separate acceptance schema: the supplied MySQL account cannot create one. The fresh-schema migration, application startup, and SMTP persistence were verified against the dedicated load-test schema.
- Eight simultaneous deliveries to one mailbox have a high temporary-failure rate due to MySQL deadlocks. This is the next optimization target. The current test is insufficient to claim any throughput gain over the old SQLite deployment, and the concurrent result should block a claim of improved concurrency.

The original data archive can be restored by stopping the Supervisor program and swapping the two application directories back. MySQL test data remains in the new schema until explicitly removed.
