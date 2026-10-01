# 第四阶段：后台查询与轮询（草稿）

## 实现范围

在既有 system_settings 使用 internal.search.backfill.cursor.v2 保存 (ReceivedAt, Id)，失败记录到期优先重试，扫描沿游标推进，扫描完成后循环核验。检索记录/失败状态持久化完成后推进游标，损坏游标安全重扫；并发进程竞争造成重复扫描仍可幂等。新邮件提交后合并唤醒只加速空批等待，数据库轮询继续兜底；非空批最多 16 封且至少间隔 1 秒，单并发、资源不足暂停。

未重做已有 IDLE 协调器，未默认启用 Direct MX 连接池。没有新增业务表，既有检索数据保留。

## 对照与准确归因

protocol-backfill-control SHA256 为 f6b81b941c2a52ca047681b0fff76020b61e1320ac32d79977f791c218c801d8，其余应用源码与 protocol-final 一致，只保留旧补建实现。旧基线空批等待已经 30 秒，不能声称本阶段把一秒空轮询改为三十秒。新实现增加检查点与到期重试查询，空闲 SQL 次数可能上升；收益必须由历史尾部扫描与 backlog 证据证明。

## 已有与待完成验收

safe-final 回归包含相同 ReceivedAt 多记录游标、失败到期、资源不足及重启续作；最新 protocol-final 完整回归已通过 445 项、0 跳过。源码保留缺失/失败/旧版/不完整检索数据的原文回退，尚未补建不能漏协议结果。

pending：100k 不同正文中的 112 条尾部 backlog、候选/旧补建控制各五轮，完成时间、SQL 次数/rows_examined、分配与内存；旧扫描、游标头尾扫描、Failed/RetryAfter 查询的实际 EXPLAIN；空闲 SQL；损坏游标和多进程竞争日志；补建与前台混合负载。数据库现有 message_search_records 只有 MessageId 主键，是否需要失败重试复合索引须由计划与 rows_examined 证明。

## 回退

关闭检索功能后仍可原文搜索；回退旧补建产物保留检索表、检索记录及游标键，旧代码可忽略游标。后台源码无独立开关时可使用匹配 protocol-backfill-control，保留协议修复与前台优化，比回退已知 STORE/EXPUNGE 有误基线更可控。不得删除失败记录来制造 backlog 下降；不得提高并发或缩短非空批限速来冒充游标收益。

## 当前候选版本

最终候选 `protocol-final` SHA256 为 `1be69e2e04c85a91d65327f06e29bea85612d3a53cbfade4b066b9bad2cf9023`。Release 在 Ubuntu-26.04 WSL2 构建，匹配 Portable PDB；实际 SQLite/MySQL 回归 445 项全部通过，0 失败、0 跳过，TRX 原始证据已保存。上一产物 safe-final 的 444 项通过及性能数值仅属于旧产物。

## 边界与证据状态

基线包含开始时完整工作区，保留原有修改。服务、数据库、负载发生器均在 192.168.2.8，正式性能关闭应用性能采集与诊断 API，原生工具另设窗口。服务器约 15.37 GiB、4 核、无 Swap；没有真正整机 1 GiB 环境。协议输出、持久化、租约、配额及资源保护保持要求不变。按用户最新要求，最终两种持续负载各 10 分钟，不能声称最终版本完成 30 分钟验收。本文是分阶段草稿，所有 pending 均须在主报告完成后补入实际证据。

## 最终默认配置短对照补充

protocol-final Prepared=false 短协议五轮已完成，详见 `2026-10-01-wsl-protocol-final-default-comparison-draft-185033.md`。SUBJECT 未命中 P95 +4.82%，其他 SEARCH/FETCH 条件均改善；五次样本 P95 为最大值，最终默认持久对照已补充，16 个组合五轮 P95 均改善；大邮箱仍须独立补充。445 实际 SQLite/MySQL 回归通过，0 跳过。
