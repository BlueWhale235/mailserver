.\scripts\build.ps1 -Configuration Release


.\scripts\publish-portable.ps1 -Runtime linux-x64

.\scripts\publish-portable.ps1 -Runtime win-x64,linux-x64,linux-arm64

bash scripts/build-linux-release.sh v2.0.0



.\scripts\publish-plugins.ps1 -Configuration Release

# 小内存验收脚本（待外部环境运行）

- `acceptance-mysql-84.sh`：启动独立 MySQL 8.4 容器，在专用 `mailserver_acceptance` 数据库运行迁移、配置版本竞争及双 Worker 租约测试；结果写入 `builds/acceptance/mysql-*`。
- `acceptance-linux-512m.sh`：从当前源码构建容器，在 512 MiB cgroup 内运行 SQLite `run all`，发送普通及大附件邮件并保持慢连接，同时更新配置；保存 RSS、堆、GC、吞吐、P95 和 OOM 记录到 `builds/acceptance/linux-512m-*`。默认持续 300 秒，可用 `DURATION_SECONDS`、`ATTACHMENT_BYTES` 调整。

二者要求 Linux 上可用的 Docker；第一个还要求 .NET SDK，第二个要求 Python 3。脚本交付不等于验收通过。当前机器缺少 Docker，尚未运行；基线提交为 `2180361`，性能对比需在相同硬件与参数下另跑该提交。
