# 第三阶段：入站存储与活跃任务预算（草稿）

## 实现范围

SMTP DATA 边写暂存边计算哈希，以池化 64 KiB 缓冲合并写入。PreparedMessageContent 明确保存路径、长度、hash 与文件所有权；入站同卷内容移交给内容存储，普通 Stream 和跨设备保留校验复制。Linux 在移动前使用设备编号验证同盘，避免 .NET File.Move 隐式跨卷复制被误当原子移交；不能确认设备或其他平台保守走复制。保留原文、去重、大小限制、拒收/取消/回滚清理责任。

Storage:PreparedInboundEnabled 默认 false；InboundTemporaryPath 为空且开启时使用内容存储下 .incoming。连接额度与重操作额度分开，BODY 回退、MIME FETCH、入站/APPEND 解析与补建共享重操作许可，避免嵌套获取。有界等待最多 30 秒，取消可退出，释放/调整许可直接唤醒等待者。远端活跃入站/投递 4，重操作 2；小内存档原默认 1。

## 版本与安全修正

早期 nodelay-final 完成两种 30 分钟采集负载，但没有后来新增的 Linux 同设备保护与 STORE/EXPUNGE 修复，不能归为最终产物稳定性。safe-final 增加实际 /dev/shm 与 /tmp 跨设备测试，444 回归通过；protocol-final 再补协议映射修复。最终在 Prepared=false 下执行两种 10 分钟负载。

## 已有与待完成验收

safe-final 相同二进制开关各五轮、每尺寸 80 封：10 MiB 开启 P95 715.084→654.449 ms，五轮均改善，接受吞吐 7.631→7.847/s。1 MiB P95 187.783→211.265 ms，吞吐 27.342→26.066/s；聚合 P95 增加 12.51%，但五轮只有一轮 +84.6% 明显异常，其余均在 ±5% 内，不能断言稳定可重复退化。收益尚不明确，保守保持默认关闭。

pending：最终默认配置短对照已完成；最终产物存储原生文件诊断证明 10 MiB 原文单次完整写入、同盘移动及未生成额外完整副本；取消、重复内容、磁盘异常现有回归证据归档；两种 10 分钟 RSS/GC/LOH/分配/暂停/cgroup/健康/Swap 数据。不把 cgroup file 缓存当托管堆，不把 RSS 与 cgroup 内存相加。gcdump 在独立空闲窗口，说明完整 GC 的影响。

## 回退

关闭 PreparedInboundEnabled 并重启恢复普通暂存/校验复制，已保存原文无需转换。MaximumHeavyOperations 维持原资源档与经过验证的 2，不能通过放大到几十并发掩盖性能退化。源码许可唤醒等变更回退使用匹配产物；取消有界等待、配额账本及资源保护不得关掉作为所谓回退。

## 当前候选版本

最终候选 `protocol-final` SHA256 为 `1be69e2e04c85a91d65327f06e29bea85612d3a53cbfade4b066b9bad2cf9023`。Release 在 Ubuntu-26.04 WSL2 构建，匹配 Portable PDB；实际 SQLite/MySQL 回归 445 项全部通过，0 失败、0 跳过，TRX 原始证据已保存。上一产物 safe-final 的 444 项通过及性能数值仅属于旧产物。

## 边界与证据状态

基线包含开始时完整工作区，保留原有修改。服务、数据库、负载发生器均在 192.168.2.8，正式性能关闭应用性能采集与诊断 API，原生工具另设窗口。服务器约 15.37 GiB、4 核、无 Swap；没有真正整机 1 GiB 环境。协议输出、持久化、租约、配额及资源保护保持要求不变。按用户最新要求，最终两种持续负载各 10 分钟，不能声称最终版本完成 30 分钟验收。本文是分阶段草稿，所有 pending 均须在主报告完成后补入实际证据。

## 最终默认配置短对照补充

protocol-final Prepared=false 短协议五轮已完成，详见 `2026-10-01-wsl-protocol-final-default-comparison-draft-185033.md`。SUBJECT 未命中 P95 +4.82%，其他 SEARCH/FETCH 条件均改善；五次样本 P95 为最大值，最终默认持久对照已补充，16 个组合五轮 P95 均改善；大邮箱仍须独立补充。445 实际 SQLite/MySQL 回归通过，0 跳过。
