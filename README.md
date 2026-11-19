# MailServer

## 项目简介

MailServer 是一个基于 .NET 10 的自托管邮件服务器，提供 SMTP 收发、IMAP 邮箱访问、管理 API 和 Vue 管理后台。SMTP、IMAP、API 和投递 Worker 可以由 CLI 一起启动，也可以独立运行。

## 部署方式

**Windows 源码包。** 解压后双击 `start-windows.cmd`。脚本会检测符合 `global.json` 的 .NET SDK 和 Node.js 24，缺少时下载并解压到项目的 `.cache/windows-tools/`，再构建并启动。SDK、Node.js、NuGet 和 npm 缓存都留在项目的 `.cache/` 中，不修改系统环境。首次运行需要联网。

Windows 脚本默认只监听本机：管理后台 `http://127.0.0.1:8080/admin/`、SMTP `127.0.0.1:2525`、IMAP `127.0.0.1:1143`。初始后台账号为 `admin`，密码为 `MailServer-Local!2026`。邮箱认证需另外配置证书和邮箱账号。配置和 SQLite 数据库保存在 `data/windows-local/`，重复运行会保留账号和数据。按 Ctrl+C 停止服务。

首次启动会创建两个示例域名（`demo.example.test`、`lab.example.test`）、三个邮箱账号、一个收件别名和五封本地邮件。登录后台后，可以在「域名」「邮箱账号」查看账号和别名，在「运维中心 → 邮件查询」预览正文和附件列表、下载邮件原文。示例不产生外发投递。已有 SQLite 实例可手动执行 `mailserver demo seed --config <配置文件>` 补充示例。

**单文件部署。** 按[构建与发布说明](docs/deployment.md#本地构建与版本)生成 Linux x64 可执行文件 `mailserver`，将它放到新的可写目录。首次运行时初始化配置、数据库和管理员，保存终端显示的初始密码，再启动服务：

```bash
chmod +x mailserver
./mailserver init
./mailserver run all
```

管理后台默认地址为 `http://127.0.0.1:8080/admin/`。后台已随服务发布。Windows 使用 `mailserver.exe` 执行相同的子命令，发布方法见[构建与发布脚本](scripts/readme.md)。

**Docker Compose。** 在源码目录参照 [mailserver.example.json](mailserver.example.json) 和 [.env.example](.env.example) 准备 `mailserver.json` 和 `.env`，按[部署指南](docs/deployment.md#docker)启动 MySQL、初始化数据库和管理员，再启动各服务。镜像构建会编译管理后台，需在 Dockerfile 的构建阶段提供 Node.js 和 npm。

**分机部署。** 各节点使用同一 MySQL 业务库，邮件原文通过共享目录或 S3 兼容对象存储访问，配置见[分机部署指南](docs/distributed-deployment.md)。SQLite 适用于单机且只运行一个投递 Worker 的场景。

当前版本需要全新的业务数据库，旧版 GUID 数据库无法直接升级，详见[本机日志与雪花 ID](docs/local-logs-and-snowflake-ids.md)。域名、端口和证书配置见[服务配置](docs/configuration.md)。

## 开发文档

本地开发使用 .NET SDK 10.0.301（见 [global.json](global.json)）和 Node.js 24。在仓库根目录运行以下命令可构建项目并执行 .NET 测试：

```powershell
./scripts/build.ps1 -Configuration Release -Test
```

Linux 下使用 `TEST=true sh scripts/build.sh`。管理后台位于 `src/MailServer.Admin`，可在该目录运行 `npm ci` 安装依赖、`npm run dev` 启动开发服务器、`npm test` 运行前端测试。

演示数据入口在 [DemoData.cs](src/MailServer.Cli/DemoData.cs)：域名、账号和别名复用管理服务，邮件通过邮箱追加服务写入 SQLite 和原文存储。[OperationsMailService.cs](src/MailServer.Infrastructure/OperationsMailService.cs) 负责后台查询、正文预览和原文下载。

- [架构总览](docs/architecture-overview.md)
- [本地启动、CLI 与数据库](docs/getting-started.md)
- [前端组件](docs/development/frontend-components.md)
- [插件开发与安装](docs/plugins.md)
- [完整文档索引](docs/README.md)
