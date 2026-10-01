# WSL 构建与远端协议压测 Prompt

下面内容可复制到新的对话。访问凭据通过受限文件单独提供；不要复制进这个文档。

```text
请部署和评估 D:\smtp-server 当前完整工作区，完成真实协议负载、原生 .NET 诊断、前后对比和报告。本任务先评估，不自动修改业务代码。

使用 Ubuntu-26.04 WSL2，在 /mnt/d/smtp-server 编辑、构建和打包，通过 WSL SSH 编排 root@192.168.2.8。服务、MySQL 回归、协议负载发生器和诊断均在这台远端机器执行。部署目录 /mnt/storage/mailserver，管理端口 HTTPS 5000；MySQL 127.0.0.1:3306，数据库和用户 app-mailserver。凭据从已有受限文件读取；缺少时仅询问缺失项。禁止在命令行、源码、日志、报告中放入密码、私钥或真实完整连接串。

先读取 docs/performance 中最新带时分秒的 WSL 报告，再参考 2026-10-01-large-memory-comparison-025108.md、2026-10-01-runtime-efficiency-iteration-003257.md，以及 builds/performance/wsl-iteration-20261001、lan-20261001、round2-20260930。优先复用 scripts/remote-efficiency-round2.py、remote-efficiency-monitor.py。历史客户端位置不同，只能作为跨环境参考，不能算代码收益。检查脚本的清理范围和平台参数，禁止盲目执行历史清库操作。

保留开始时完整工作区、Git HEAD、已有改动、产物 SHA256、匹配 Portable PDB、配置、数据库规模和资源限制。使用 Release + Portable PDB，记录 SDK、运行时和诊断工具版本。检查实际 CPU/内存/存储/其他服务，不能假定是 32 GB。保留所有无关数据和服务；必要端口仅开放到测试来源。

正式吞吐比较关闭应用性能采集和 EfficiencyDiagnostics，不从诊断 API 读取性能。使用 dotnet-trace、dotnet-counters、进程/cgroup、磁盘和 MySQL performance_schema；原生诊断另设窗口。采样线程时间包含等待，不能当 CPU 百分比。gcdump 仅在独立空闲窗口执行，并说明会触发完整 GC。服务与负载客户端同机时，单独记录客户端 CPU、内存、I/O。

先用本轮执行方式建立完整基线。候选与基线交替各测五轮，固定数据、预热、并发、配置、客户端位置与计时边界。记录成功/拒收/限流、P50/P95、吞吐、SQL 次数与返回行数、原文打开次数、分配量、GC、峰值内存及错误。不能用微基准替代端到端结论；未满足 P95/吞吐无超过 5% 的可重复退化时，不宣称全面通过。

A. 固定 6 封邮件：2×1 MiB、2×10 MiB、Unicode/编码头及空正文。测 SUBJECT 命中/未命中、BODY 尾部命中/未命中、FETCH HEADER/全文/ENVELOPE BODYSTRUCTURE。新收信进入独立邮箱，不能污染固定数据集。
B. SMTP 收信并发 2/4/8/16，各五轮；每轮每种大小分别 16 封。可暂时放宽测试来源连接频率，不能放宽持久化、配额或活跃资源保护；测试后恢复。区分冲突重试事件、发生重试的不同邮件和最终失败；现有日志无法关联邮件时明确未知，不能将派生回滚错误重复计为失败。
C. 持久 IMAP 并发 1/2/4/8，每种搜索条件每档五组，每组 16 请求；TLS、登录、SELECT 排除在操作计时之外，连接建立成本另报。
D. 大邮箱 1 万/10 万条共享引用，以及 1 万/10 万封不同 MessageId/正文（1–4 KiB，中文、编码头、附件样本）。两类分开报告。测 SUBJECT/BODY/UNSEEN、STATUS、单封与区间 COPY/MOVE，五轮；记录 SQL、返回行数、索引状态、原文打开和内存。只调整本轮测试账户配额，清理后正确恢复。
E. 同邮箱 IDLE 1/100 订阅者各观察至少 35 秒；对比刷新 SQL，验证 APPEND、删除、最后退订与重连。区分提交后通知时间、APPEND ACK 代理时间与端到端时间；有采集开销时说明。
F. 按当前要求，两种负载各至少 10 分钟：第一种约每 10 秒一轮，8 封收信、4 次搜索、2 次 FETCH、健康检查；第二种连续 SMTP 4 并发、BODY 搜索 2 并发、全文 FETCH 1 并发。分开报告 RSS、GC committed、LOH、分配量/暂停、cgroup anon/file、可用内存、Swap、CPU、磁盘、MySQL 和错误。不能把文件缓存当堆，也不能把 RSS 与 cgroup 相加。
G. 使用受控 SMTP 接收端验证真实队列连续 20 封、451 临时失败后队列重试、550 永久拒收；核对实际接收的唯一邮件。不能给真实第三方发送压测邮件。受控无 TLS 测试不等于真实 Direct MX/TLS 验收；连接复用保持原默认关闭。

资源停止条件：大内存可用内存低于 2 GiB 持续 10 秒；真正整机约 1 GB 可用内存低于 96 MiB 持续 10 秒，或服务 Swap 超过 512 MiB。任一 OOM、异常重启、健康连续失败三次即停止并保存证据。无 Swap 不能因 SwapFree=0 误停。仅应用 1 GiB cgroup 属补充，不等于整机 1 GB；没有独立小内存环境则明确未验收。10 分钟属于短时压力验证，不代替完整 30 分钟稳定性验收。不要调整宿主 WSL 或无关容器资源。

交付 docs/performance/Asia-Shanghai-YYYY-MM-DD-主题-HHmmss.md 和独立 builds/performance 原始证据，保留 nettrace、有效完整计数器 JSON、PDB、SQL、TRX 和失败日志。给同机前后表、历史环境比较、可重复收益、瓶颈证据和改进优先级。注明预热、客户端位置、索引完整度、采样限制和未覆盖项。清理仅按本轮测试标识/确切 ID，写正确配额释放账本；保留审计、投递记录、部署和其他业务数据。恢复临时防火墙、连接保护、配置、配额，确认健康；清理临时凭据与 SSH 工作密钥。删除若被工具策略拒绝，报告实际状态，禁止绕过。
```
