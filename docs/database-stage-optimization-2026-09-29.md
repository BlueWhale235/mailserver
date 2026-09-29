# SMTP 数据库阶段优化与连接池对照（2026-09-29）

## 结论与范围

提供的线上接口中，一次 SMTP `DATA` 的只读性能追踪显示 `database.open` 共 10 次、合计约 0.23 ms，`database.save-changes` 约 1,785 ms。前者是 EF 的逻辑打开事件，不能据此推断建立了 10 条物理连接。该样本以及过去一天 SMTP `database.open` 40 次、合计约 0.72 ms 的聚合表明，目前没有证据支持调大连接池。本轮保持 SQLite/MySQL 连接池默认配置、并发限制和数据库结构不变。

`SaveChangesAsync` 现在细分为 `database.detect-changes`、`database.prepare-entries`、`database.persist`，并保留原来的 `database.save-changes` 总阶段。没有路由配置变更时，显式检测后暂时关闭 EF 自动检测，避免 `Entries()` 和 EF 保存重复扫描；无论成功、失败或取消，都会恢复原设置。路由版本更新路径继续使用原有自动检测和事务流程。规则缓存增加 `cache.populate`，并分别记录规则数量、大小估计、读取与编译，现阶段只用于定位冷加载开销。

## 基准方法

环境：Windows 11，Ryzen 9 7940H，约 32 GiB 内存，.NET SDK 10.0.301 / runtime 10.0.9，Release，SQLite。使用同一基准代码和配置，在代码修改前后各预热 1 次、测量 5 次。SMTP 用例经 `InboundMessageService.AcceptAsync` 接收 1 MiB、单本地收件人的内存流邮件，写入本机临时文件及 SQLite；SQL 次数和连接打开次数来自性能追踪。P95 是 5 次样本的最大值，不代表长期负载的稳定分位数。分配量是进程级测量，峰值工作集覆盖测试进程生命周期。

| SMTP 本地收信 | 修改前 | 修改后 |
| --- | ---: | ---: |
| 整体耗时中位数 | 21.97 ms | 21.85 ms |
| 整体耗时样本 P95 | 63.12 ms | 57.04 ms |
| `SaveChanges` 中位数 | 4.48 ms | 3.75 ms |
| `SaveChanges` 样本 P95 | 16.56 ms | 15.17 ms |
| 分配量中位数 | 15,360,520 B | 15,366,728 B |
| SQL 次数中位数 | 16 | 16 |
| 逻辑连接打开次数中位数 | 2 | 2 |
| 峰值工作集 | 156,016,640 B | 150,392,832 B |

修改后，`SaveChanges` 中位耗时约下降 16%，但整体 SMTP 耗时没有可确认的提升；另一轮修改后测量为整体 22.41 ms、保存 4.19 ms，说明样本仍有波动。修改后阶段中位数：检测 0.10 ms、准备实体 0.01 ms、EF 持久化 3.64 ms；SQL 执行 3.27 ms，逻辑连接打开 0.07 ms。保存阶段的剩余耗时主要落在 EF 持久化和 SQL 执行中。生产环境曾出现的 1.8 秒保存耗时，不能由本机结果解释；需要部署新阶段数据后再分析。

连接池对照以同一 SQLite 文件顺序执行 100 次打开/关闭，分别在 `Pooling=False` 和 `Pooling=True` 下预热 1 轮、测量 5 轮。关闭时中位 36.11 ms、样本 P95 36.37 ms、分配 43,144 B；开启时中位 0.04 ms、样本 P95 0.07 ms、分配 19,144 B。这个微基准说明连接池在本机有效，不代表真实并发负载或生产 MySQL 的收益。

## 验证与限制

后端 Release 回归 399 项通过，前端 203 项通过，前端构建通过。新增 SQLite 用例验证实体图、属性修改、审计记录、路由版本更新，以及失败和取消后自动检测状态恢复。MySQL 验收用例和独立连接池/SMTP 基准已经扩展，但当前环境没有 `MAILSERVER_TEST_MYSQL_CONNECTION`，且无 Docker 或本地 MySQL，因此 **MySQL 实库未验证**；基准中的配置缺失提示不能算通过。

重跑方式：

```powershell
$env:MAILSERVER_DATABASE_BENCHMARK='1'
dotnet test tests/MailServer.Tests/MailServer.Tests.csproj -c Release --filter FullyQualifiedName~DatabaseStageBenchmarks --logger 'console;verbosity=detailed'
```

MySQL 验收需先设置指向独立 `mailserver_acceptance` 数据库的 `MAILSERVER_TEST_MYSQL_CONNECTION`，再运行 `MySqlAcceptanceTests` 和上面的基准。基准会向该验收库写入数据，不能指向生产库。发布后重点比较 SMTP `DATA` 的 `database.persist`、SQL、规则缓存加载、整体 P95 与错误率；不要把逻辑连接打开次数当作物理连接数。
