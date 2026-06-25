# MailServer

一个面向 .NET 10 的模块化 C# 邮件服务器基础实现。SMTP、IMAP、管理 API、Blazor 管理后台与投递 Worker 可以分别运行，也可以通过便携 CLI 在单进程中一起运行。

## 主要能力

- SMTP：25/465/587、STARTTLS、隐式 TLS、AUTH PLAIN、防开放中继、流式 DATA、大小和连接限制。
- IMAP：IMAP4rev2/IMAP4rev1 常用命令、UID、Flags、FETCH、SEARCH、STORE、COPY、MOVE、APPEND、EXPUNGE、IDLE。
- 外发：上游 Relay、直接 MX、重试租约、退信、投递历史、可选 DKIM。
- 数据库：SQLite、PostgreSQL、MySQL 三种 EF Core Provider 和独立迁移程序集。
- 管理：`/api/v1` JWT/API Key API，以及 `/admin` Blazor Web App。
- 安全：管理员 Cookie、CSRF、版本化密码哈希、PEM 证书/私钥、TLS 1.2/1.3。
- 扩展：启动时加载的受信任 DLL 插件。
- 运维：单文件 CLI、数据库更新、诊断、账号管理和 Docker Compose。

SQLite 适合本机开发和单 Worker 测试；生产建议 PostgreSQL 16+ 或 MySQL 8.4 LTS+。数据库切换使用全新数据库，本项目不复制或转换不同数据库中的数据。

## 快速开始

构建：

```powershell
./scripts/build.ps1 -Configuration Release -Test
```

所有项目的编译、中间文件和发布产物统一放在根目录的 `builds`：

```text
builds/
  bin/       按项目分组的程序集
  obj/       按项目分组的中间文件
  publish/   单文件便携发布
```

不再在每个项目目录中生成独立的 `bin` 和 `obj`。项目之间仍使用独立子目录，避免配置文件和同名依赖互相覆盖。

初始化便携配置：

```powershell
dotnet run --project src/MailServer.Cli -- init
```

该命令会：

- 创建当前目录的 `mailserver.json`；
- 选择 SQLite、PostgreSQL 或 MySQL；
- 配置后台地址、端口和 PEM 路径；
- 生成随机 JWT 密钥；
- 应用所选 Provider 的数据库迁移；
- 创建管理员，并且只显示一次初始密码；
- 在 Unix 设置配置文件权限为 `0600`，在 Windows 限制为当前用户访问。

启动全部服务：

```powershell
dotnet run --project src/MailServer.Cli -- run all
```

后台地址默认为 `http://127.0.0.1:8080/admin`。也可单独启动：

```text
mailserver run api
mailserver run smtp
mailserver run imap
mailserver run worker
```

所有命令默认读取 `./mailserver.json`，也支持 `--config <path>` 或 `MAILSERVER_CONFIG`。

## CLI

```text
mailserver init
mailserver doctor
mailserver config validate
mailserver db status
mailserver db update
mailserver admin create <name> --password <value>
mailserver admin password <name> --password <value>
mailserver domain add <domain> --mode Relay
mailserver account create <address> --password <value>
mailserver account password <address> --password <value>
mailserver account enable|disable <address>
mailserver run all|api|smtp|imap|worker
```

服务启动不会自动修改数据库结构。如果存在待应用结构版本，服务会提示运行：

```text
mailserver db update
```

## 数据库

配置格式：

```json
{
  "Database": {
    "Provider": "Sqlite"
  },
  "ConnectionStrings": {
    "MailDatabase": "Data Source=data/mailserver.db"
  }
}
```

Provider 值：

- `Sqlite`：本机开发测试，投递租约只支持一个 Worker。
- `PostgreSql`：使用 `FOR UPDATE SKIP LOCKED`，适合多 Worker。
- `MySql`：使用 MySQL 8 的 `FOR UPDATE SKIP LOCKED`。

三套迁移分别位于：

- `MailServer.Migrations.Sqlite`
- `MailServer.Migrations.PostgreSql`
- `MailServer.Migrations.MySql`

## 后台与端口

```json
{
  "AdminServer": {
    "BindAddress": "127.0.0.1",
    "Http": {
      "Enabled": true,
      "Port": 8080
    },
    "Https": {
      "Enabled": false,
      "Port": 8443,
      "RedirectHttp": true
    }
  }
}
```

后台至少要启用一个端点。HTTP 适合回环开发；生产应启用 HTTPS。若后台通过非回环 HTTP 访问，页面会显示安全警告。

SMTP 和 IMAP 每个端点也可以配置绑定地址：

```json
{
  "Smtp": {
    "Endpoints": [
      {
        "Address": "0.0.0.0",
        "Port": 25,
        "ImplicitTls": false,
        "RequireAuthentication": false
      }
    ]
  }
}
```

## PEM 证书

```json
{
  "Certificates": {
    "Mail": {
      "CertificatePath": "certificates/mail-chain.pem",
      "PrivateKeyPath": "certificates/mail-key.pem",
      "PrivateKeyPassword": ""
    },
    "Admin": {
      "CertificatePath": "certificates/admin-chain.pem",
      "PrivateKeyPath": "certificates/admin-key.pem",
      "PrivateKeyPassword": ""
    }
  }
}
```

- 证书文件可以包含完整证书链。
- 支持 PKCS#8、RSA PEM 和加密私钥。
- SMTP 与 IMAP 共用 `Mail` 证书；后台使用 `Admin` 证书，也可以让两项指向同一组文件。
- 未配置邮件证书时，隐式 TLS 端点不会启动，STARTTLS/AUTH 不会开放。
- 启动时会验证证书与私钥、有效期和 HTTPS 配置。

## Blazor 管理后台

`/admin` 提供：

- 仪表盘和健康概览；
- 域名、外发模式和 DKIM 配置；
- 邮箱账号、密码、状态和配额；
- 地址别名；
- API Key 创建与撤销；
- 投递队列查询与重试；
- MX、SPF、DKIM、DMARC 诊断；
- 管理员密码修改。

浏览器使用 HttpOnly、SameSite Cookie；外部 API 保留 JWT 与 `X-Api-Key`。

## Docker

先使用 CLI 生成 `mailserver.json`，再配置 `.env`。

PostgreSQL：

```bash
docker compose --profile postgres up -d postgres
docker compose --profile postgres --profile tools run --rm cli db update
docker compose --profile postgres --profile tools run --rm cli admin create admin --password 'replace-this-password'
docker compose --profile postgres up --build -d api smtp imap worker
```

MySQL：

```bash
docker compose --profile mysql up -d mysql
docker compose --profile mysql --profile tools run --rm cli db update
docker compose --profile mysql --profile tools run --rm cli admin create admin --password 'replace-this-password'
docker compose --profile mysql up --build -d api smtp imap worker
```

数据库类型、连接字符串、后台端口、邮件端口和 PEM 路径均可用环境变量覆盖。

## 单文件发布

Windows：

```powershell
./scripts/publish-portable.ps1
```

Linux/macOS：

```bash
./scripts/publish-portable.sh
```

默认只发布当前操作系统与 CPU 架构，以减少磁盘使用。需要一次生成全部目标时：

```powershell
./scripts/publish-portable.ps1 -Runtime win-x64,linux-x64,linux-arm64
```

输出位置：

- `builds/publish/win-x64`
- `builds/publish/linux-x64`
- `builds/publish/linux-arm64`

发布所需的大型 RID 中间文件会写入系统临时目录，并在完成后删除；`builds` 中只保留最终单文件。

如果只需要部署文件、不需要保留增量构建缓存：

```powershell
./scripts/publish-portable.ps1 -RemoveBuildCache
```

执行后 `builds` 通常只剩约 120 MiB 的当前平台单文件；下次编译会重新生成 `bin/obj`。

产物为 self-contained 单文件 CLI，不要求目标机器预装 .NET。

## 上线前

- 完成 MX、A/AAAA、PTR、SPF、DKIM、DMARC 配置。
- 使用受信任插件或隔离服务增加反垃圾与病毒扫描。
- 配置数据库、邮件内容和 Data Protection 密钥备份。
- 测试 Thunderbird、Outlook、Apple Mail 与 MailKit。
- 监控队列深度、磁盘、TLS 有效期、认证失败和数据库健康。
- 对 SMTP/IMAP 做协议模糊测试和断电/崩溃恢复测试。

这是可运行的邮件服务器基础，但尚未宣称通过所有 RFC 可选扩展和客户端认证矩阵。
