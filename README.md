# MailServer

一个面向 .NET 10 的模块化 C# 邮件服务器。SMTP、IMAP、JSON API、Vue/Vuetify 管理后台和 Direct MX 投递 Worker 既可以独立运行，也可以通过便携 CLI 统一运行。

## 主要能力

- SMTP：25/465/587、STARTTLS、隐式 TLS、AUTH、防开放中继及流式 DATA。
- IMAP：UID、Flags、FETCH、SEARCH、STORE、COPY、MOVE、APPEND、EXPUNGE 和 IDLE。
- 外发投递：仅 Direct MX，支持重试租约、退信、分阶段诊断、TLS 策略和 DKIM。
- 数据存储：SQLite、PostgreSQL、MySQL，以及独立的 EF Core 迁移程序集。
- 管理与安全：`/api/v1`、`/admin`、JWT/API Key、PEM 证书和 TLS 1.2/1.3。
- 扩展与运维：插件系统、单文件 CLI、诊断、Docker Compose 和便携发布。

SQLite 适合本机开发和单 Worker 测试；生产建议使用 PostgreSQL 16+ 或 MySQL 8.4 LTS+。

## 快速开始

```powershell
./scripts/build.ps1 -Configuration Release -Test
dotnet run --project src/MailServer.Cli -- init
dotnet run --project src/MailServer.Cli -- run all
```

管理后台默认地址为 `http://127.0.0.1:8080/admin`。所有命令默认读取 `./mailserver.json`，也支持 `--config <path>` 或 `MAILSERVER_CONFIG`。

## 文档

- [文档索引](docs/README.md)
- [快速开始、CLI 与数据库](docs/getting-started.md)
- [服务配置](docs/configuration.md)
- [插件开发与安装](docs/plugins.md)
- [部署与上线](docs/deployment.md)
- [前端组件设计](docs/development/frontend-components.md)
- [邮件系统异常场景测试清单](docs/testing/mail-system-edge-cases.md)

## 项目结构

```text
src/       服务、CLI、领域模型和数据库迁移
plugins/   内置插件与插件示例
tests/     自动化测试
scripts/   构建与发布脚本
docs/      使用、部署、开发和测试文档
builds/    统一的编译、中间和发布产物
```
