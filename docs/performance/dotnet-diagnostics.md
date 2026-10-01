# 下一轮使用 .NET 工具诊断

新生成的报告统一使用 `YYYY-MM-DD-报告名称-HHmmss.md`，时分秒按 Asia/Shanghai 记录在文件名末尾；原始采集文件另行保存。

用户已确认下一次采用 **Release + Portable PDB**，诊断改用 .NET 工具，不依赖应用诊断 API。二进制与同一构建的符号一起部署，不使用 Debug 构建作性能基线。无需为了使用诊断工具关闭编译优化。

具体采用 **dotnet-trace 采集 + Windows PerfView 分析**；保留原始 `.nettrace`、同版 PDB、负载参数和工具版本。先使用短窗口定位耗时堆栈、GC 暂停、分配类型及锁/线程等待，再在不采集 trace 的条件下验证性能变化。`dotnet-counters` 作为低开销的持续指标补充。

## 发布与部署

发布时显式保留符号，并把同一构建的所有 PDB 与二进制一并保存：

```powershell
dotnet publish src/MailServer.Cli/MailServer.Cli.csproj -c Release -p:PublishProfile=DiagnosticLinux -o builds/publish/diagnostic-release-linux-x64
```

发布配置 `DiagnosticLinux.pubxml` 显式开启 `PreservePublishSymbols`，避免现有发布清理步骤删除 PDB。保持 Release，不在一次前后效率比较中混用不同构建类型。记录二进制、PDB、工具版本与运行时版本，确认部署符号来自相同源码和构建。保持 .NET 运行时诊断可用；不需要向公网开放诊断端口。

使用现有便携发布脚本时，加上 `-IncludeSymbols`：`./scripts/publish-portable.ps1 -Runtime linux-x64 -IncludeSymbols`，脚本的构建和收尾清理都会保留符号。

2026-09-30 已实际发布验证 `DiagnosticLinux` 配置：构建成功，产物保留 10 个项目的 Portable PDB，并核验了 PDB 格式标识。产物在 `builds/publish/diagnostic-release-linux-x64/`；本轮远端持续压测二进制未被这批新产物替换。

## 采集

在远端以相同用户通过本地进程诊断通道连接。先确认 `dotnet-counters`、`dotnet-trace` 可用并记录版本。自包含应用同样支持工具附加；可用官方独立工具包，不要求为应用安装完整 SDK。

先采集 `System.Runtime` 计数器，观察 CPU、分配速度、GC、堆和线程池。短窗口采集线程堆栈及 GC 分配事件，用 PerfView 或其他兼容分析器查看热点方法和分配类型。具体 profile 名称先检查当前工具的 `list-profiles`；新版 `collect` 的线程采样 profile 是 `dotnet-sampled-thread-time`，旧资料的 `cpu-sampling` 不可直接照搬。线程采样含等待线程，不能将全部堆栈时间都当作 CPU 时间。

```bash
dotnet-counters collect --process-id "$task_pid" --counters System.Runtime --refresh-interval 1 --format json --output runtime-counters.json
dotnet-trace collect --process-id "$task_pid" --profile dotnet-sampled-thread-time,dotnet-common --buffersize 8 --duration 00:00:01:00 --output thread-time.nettrace
dotnet-trace collect --process-id "$task_pid" --profile gc-verbose --buffersize 8 --duration 00:00:00:30 --output allocations.nettrace
```

计数器采集在测试结束时正常停止，保留工具输出及丢事件提示。1 GB 环境短 trace 使用 8 MiB 缓冲起步；默认 trace 缓冲为 256 MiB，会明显干扰小内存测试。若丢事件，先降低事件量或缩短窗口，再评估增加缓冲，所有前后比较使用相同采集条件。

本轮验证 PerfView 3.2.8 的 `userCommand NetperfToSpeedScope file.nettrace` 和 `userCommand GCStats file.nettrace` 可以直接处理 EventPipe 文件；前者还生成 `file.nettrace.etlx`。事件导出使用 `userCommand DumpEventsAsXml file.nettrace.etlx`。旧的 `SaveCPUStacksAsCsv` 命令直接读取 `.nettrace` 会失败，读取转换后的 ETLX 在本轮也得到 0 个 CPU 样本，因此不能用该空 CSV 判断没有热点；采用实际生成的线程堆栈和 GC 报告。批处理运行加 `/AcceptEula /NoGui`，Windows 后台启动使用隐藏窗口。

堆引用或泄漏分析再考虑 `dotnet-gcdump` / `dotnet-dump`。`gcdump` 会主动触发 Gen2 GC，并可能带来较大事件缓冲，因此与正式延迟/吞吐验收分开采集。转储可能含邮件内容与凭据，只保存在受控位置，不提交仓库。

禁用应用的详细诊断模式与额外接受测试日志采样，仍保留正常运行日志和健康检查。工具用于 CPU、GC、分配、锁及等待分析；如果要精确统计每条 SQL 的执行时间，需要适配 EF/MySQL 事件源或数据库侧诊断，不能假定任意提供程序都会自动输出完整 SQL 时序。采集后先报告可确认的热点，再修改代码，使用相同负载复测。

参考：[dotnet-counters](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-counters)、[dotnet-trace](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace)、[dotnet-gcdump](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-gcdump)。
