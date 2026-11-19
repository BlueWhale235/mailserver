# 构建与发布

| 用途 | Windows | Linux |
| --- | --- | --- |
| 构建，可选运行测试 | `build.ps1 -Configuration Release -Test` | `TEST=true ./scripts/build.sh` |
| 便携发布 | `publish-portable.ps1 -Runtime linux-x64` | `./scripts/publish-portable.sh linux-x64` |
| 插件打包 | `publish-plugins.ps1 -Configuration Release` | `./scripts/publish-plugins.sh` |

Windows 命令从项目根目录通过 `./scripts/` 调用。
Linux 分布式存储验收：`bash scripts/test-distributed-storage.sh`，使用隔离的 MySQL / MinIO 测试容器，参见[分机部署](../docs/distributed-deployment.md)。
版本化 Linux 单文件发布：`bash scripts/build-linux-release.sh v2.0.0`。
详细说明见 [部署文档](../docs/deployment.md) 和 [插件文档](../docs/plugins.md)。
