# MailServer

一个面向 .NET 10 的模块化 C# 邮件服务器基础实现。SMTP、IMAP、管理 API、Blazor 管理后台与投递 Worker 可以分别运行，也可以通过便携 CLI 在单进程中一起运行。

## 主要能力

- SMTP：25/465/587、STARTTLS、隐式 TLS、AUTH PLAIN/LOGIN、防开放中继、流式 DATA、大小和连接限制。
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
        "Security": "StartTls",
        "RequireAuthentication": false
      },
      {
        "Address": "0.0.0.0",
        "Port": 587,
        "Security": "StartTls",
        "RequireAuthentication": true
      }
    ]
  }
}
```

`Security` 支持 `Plain`、`StartTls` 和 `ImplicitTls`。旧版 `ImplicitTls` 配置仍然兼容。SMTP 25 可以宣告 STARTTLS，但默认不提供客户端认证；若要关闭服务器间 STARTTLS，可把 25 端口显式设为 `Plain`。

SMTP 客户端认证支持 `AUTH PLAIN` 和 `AUTH LOGIN`。Thunderbird 通常可直接使用完整邮箱地址作为用户名。

## 日志

默认同时输出控制台和彩色关键字，并写入按日期切分的纯文本文件：

```json
{
  "MailServerLogging": {
    "Console": { "Enabled": true, "Color": true },
    "File": { "Enabled": true, "Directory": "logs" },
    "TimestampFormat": "yyyy/MM/dd HH:mm:ss"
  }
}
```

文件日志路径形如 `logs/2026-06-26.log`，每行以本地时间 `yyyy/MM/dd HH:mm:ss` 开头。日志级别仍使用标准 `Logging:LogLevel` 配置控制。若关闭 `MailServerLogging:File:Enabled`，服务仍会输出到控制台。

## IMAP 默认文件夹

新账号默认创建 Thunderbird 友好的文件夹：

```json
{
  "MailboxDefaults": {
    "Folders": [ "INBOX", "Sent", "Drafts", "Trash", "Junk", "Archives" ]
  }
}
```

后台“系统设置”可以修改默认文件夹，一行一个。系统会始终保留 `INBOX` 并放在第一位；已有账号会在下次 IMAP 登录时自动补齐缺失的默认文件夹。

域名级收发信规则可以把命中的邮件移动到已有文件夹。发件副本默认等待客户端 IMAP 上传 60 秒，客户端没有上传时由服务器保存；重复识别窗口默认 15 分钟：

```json
{
  "MailRules": {
    "SentCopyGraceSeconds": 60,
    "SentCopyDeduplicationMinutes": 15
  },
  "Forwarding": {
    "SrsSecret": "由 mailserver init 自动生成的独立密钥",
    "MaxHops": 5,
    "MaxAgeDays": 21,
    "MaxTargetsPerRule": 20
  }
}
```

收信规则可在保留本地副本的同时转发到域内或外部邮箱。外部转发使用带签名的 SRS 信封地址；已有安装应设置至少 32 字节的独立 `Forwarding:SrsSecret`，新配置由 `mailserver init` 自动生成。

## 外发投递

每个域名可以选择两种外发方式：

- `Relay`：把外发邮件交给上游 SMTP 服务器代发。适合家庭宽带、云服务器新 IP、没有稳定 PTR 和发信信誉的环境。
- `DirectMx`：本机直接查询收件方 MX 并投递。需要公网 25 端口、PTR、SPF、DKIM、DMARC 和较好的 IP 信誉。

上游 Relay 的 TLS 方式使用 `Relay:Security` 明确配置：

```json
{
  "Relay": {
    "Host": "MAIL.1605960.XYZ",
    "Port": 465,
    "Security": "ImplicitTls",
    "UseStartTls": true,
    "UserName": "123@1605960.xyz",
    "Password": ""
  }
}
```

`Security` 支持 `Auto`、`Plain`、`StartTls`、`StartTlsWhenAvailable` 和 `ImplicitTls`。端口 465 应使用 `ImplicitTls`；端口 587 通常使用 `StartTls`。`UseStartTls` 是旧配置兼容项，新配置优先。

## 域名证书与 DNS

SMTP/IMAP TLS 证书改为按邮件域管理：

1. 在后台“域名”中打开域名详情。
2. 设置连接主机名，例如 `mail.example.com`。
3. 上传 PEM 证书链和私钥，或填写服务器上的绝对路径。
4. 系统验证私钥匹配、有效期、Server Authentication 用途以及 SAN 是否覆盖连接主机名。

TLS 握手会优先按照客户端 SNI 精确选择域名证书；客户端不带 SNI 时，使用 `Certificates:DefaultMailHostName` 指定的默认证书，若只配置了一个域名证书则使用唯一证书。上传的证书和自动生成的 DKIM 私钥保存在 `SecurityAssets:RootPath`，默认是 `security-assets`。

域名详情页还可以：

- 生成 RSA 2048 DKIM 密钥和 DNS TXT 记录。
- 配置并生成 SPF、DMARC。
- 在线比较期望 DNS 与实际 DNS。
- 查看 SMTP/IMAP 的主机、端口、STARTTLS/SSL-TLS 和用户名格式。

## 后台 HTTPS PEM 证书

```json
{
  "Certificates": {
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
- 后台 HTTPS 证书继续使用全局 `Admin` 配置。
- 邮件域证书不再从 `Certificates:Mail` 读取。

## Blazor 管理后台

`/admin` 提供：

- 仪表盘和健康概览；
- 域名、外发模式和 DKIM 配置；
- 邮箱账号、密码、状态和配额；
- 绑定同域账号的收发信别名；
- 域名级收信和发信文件夹规则；
- API Key 创建与撤销；
- 投递队列查询、重试与删除；
- MX、SPF、DKIM、DMARC 诊断；
- 系统设置中调整投递批量、租约、最大重试次数、轮询间隔和 IMAP 默认文件夹；
- 管理员密码修改。

浏览器使用 HttpOnly、SameSite Cookie；外部 API 保留 JWT 与 `X-Api-Key`。

## 插件构建与安装

插件源码统一位于仓库根目录的 `plugins/<插件工程名>`。正式发行包含两个系统插件：

- `MailServer.MimePlugin`：MIME 与 8BITMIME 安全验证；
- `MailServer.GreylistingPlugin`：SMTP 灰名单重试策略。

单独构建两个系统插件：

```powershell
.\scripts\publish-plugins.ps1 -Configuration Release
```

```bash
./scripts/publish-plugins.sh
```

产物位于 `builds/plugins/Release/<插件工程名>`。每个插件目录必须包含
`plugin.json`、入口 DLL 和依赖文件。系统插件在 manifest 中声明
`"system": true`；第三方插件使用 `"system": false` 或省略该字段。
加载器会忽略同目录含有 `.csproj` 的源码目录，避免开发时把仓库中的插件源码
误当作已安装插件；需要联调插件时，将 `Plugins:Path` 指向上述发布产物目录。

便携发行包已经把系统插件安装到 `plugins/MailServer.MimePlugin` 和
`plugins/MailServer.GreylistingPlugin`，无需再次复制。第三方插件也使用
`plugins/<插件名>` 目录。安装、升级或删除插件文件后需要重启 API 和 SMTP
进程；插件启停、参数和执行顺序可在 `/admin/plugins` 中修改，并从下一封邮件开始生效。

Docker 镜像会在构建时安装两个系统插件到 `/app/plugins`。若要加入第三方插件，
应在自定义镜像中把已发布的插件目录复制到 `/app/plugins/<插件名>`，然后重建并重启
API 与 SMTP 容器。不要把源码仓库的 `plugins` 目录直接挂载到容器运行目录。

### Plugin Middleware 扩展点

Plugin SDK 提供以下语义型扩展点。插件接收服务器已经解析好的上下文，不需要自行解析
SMTP/IMAP 原始命令：

| Middleware | 类型 | 调用时机 |
| --- | --- | --- |
| `IConnectionMiddleware` | 策略 | TCP/TLS 连接建立后、协议问候前 |
| `ITlsEstablishedMiddleware` | 通知 | 隐式 TLS 或 STARTTLS 握手完成后 |
| `IAuthenticationMiddleware` | 认证 | SMTP/IMAP SASL 认证；可按协议声明机制 |
| `ISmtpSessionMiddleware` | 策略 | SMTP DATA 接收后的会话策略检查 |
| `ISmtpSenderMiddleware` | 策略 | `MAIL FROM` 解析和本地发件授权后 |
| `ISmtpRecipientMiddleware` | 策略 | `RCPT TO` 解析后、加入收件人集合前 |
| `IMessageContentMiddleware` | 策略 | 完整原始邮件内容读取完成后 |
| `IMessageAcceptanceMiddleware` | 策略 | 邮件写入存储前的最终接收决策 |
| `IMessageAcceptedMiddleware` | 通知 | 邮件和投递任务成功提交后 |
| `IDeliveryMiddleware` | 策略 | Worker 对外投递前 |
| `IDeliveryCompletedMiddleware` | 通知 | 投递结果写入队列状态后 |
| `IImapSessionMiddleware` | 策略 | IMAP 身份验证成功、打开邮箱前 |
| `IImapCommandMiddleware` | 策略 | IMAP 命令解析后、执行前 |
| `IAuditMiddleware` | 通知 | 审计事件产生时 |

策略 Middleware 返回 `Continue`、`Defer` 或 `Reject`，同类 Middleware 按插件管理页
保存的顺序执行。通知 Middleware 的异常只记录日志，不回滚已经完成的事务。

认证 Middleware 返回 `NotHandled`、`Success` 或 `Failure`。`NotHandled` 会继续下一个
插件；首个 `Success` 或 `Failure` 会终止认证链，避免验证失败后回退到其他提供者。
OAuth 身份最终必须映射到已有、启用且所属域活动的本地邮箱账号。

SMTP 端点可通过 `AuthenticationMechanisms` 控制允许的机制：

```json
{
  "Address": "0.0.0.0",
  "Port": 587,
  "Security": "StartTls",
  "RequireAuthentication": true,
  "AuthenticationMechanisms": [ "PLAIN", "LOGIN", "XOAUTH2", "OAUTHBEARER" ]
}
```

IMAP 使用 `Imap:AuthenticationMechanisms` 配置允许的机制。只有已启用插件实际声明的
OAuth 机制才会出现在 SMTP EHLO 或 IMAP CAPABILITY 中。

## Docker

Docker Compose 文件面向源码仓库使用，会在本机从 `src` 构建各服务镜像。便携发布包不包含源码、Dockerfile 或 Compose 构建上下文。

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

Visual Studio 中，“生成解决方案”用于开发编译和调试，不等同于部署打包。需要部署时请右键 `MailServer.Cli` 项目，选择“发布”，再选择发布配置：

- `win-x64-single-file`：Windows x64 自包含单文件包，主要可执行文件是 `mailserver.exe`。
- `win-x64-folder`：Windows x64 自包含文件夹包，包含 `mailserver.exe` 和所需 DLL。
- `linux-x64`：Linux x64 自包含单文件包，主要可执行文件是 `mailserver`。

发布目录会同时包含 `mailserver.json`、`.env`、README、证书目录、插件目录、安全资源目录、日志目录和后台静态资源目录。

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

发布包是纯运行部署包，不包含 `src`、Dockerfile、Compose 文件、解决方案文件或构建脚本。典型结构如下：

```text
mailserver(.exe)
mailserver.json
mailserver.example.json
.env
.env.example
README.md
certificates/
plugins/
security-assets/
wwwroot/
  admin.css
```

`wwwroot/admin.css` 会随包生成，便于查看和后续扩展；当前后台样式仍由程序内嵌资源提供，不依赖物理 `wwwroot` 目录。

## 上线前

- 完成 MX、A/AAAA、PTR、SPF、DKIM、DMARC 配置。
- 使用受信任插件或隔离服务增加反垃圾与病毒扫描。
- 配置数据库、邮件内容和 Data Protection 密钥备份。
- 测试 Thunderbird、Outlook、Apple Mail 与 MailKit。
- 监控队列深度、磁盘、TLS 有效期、认证失败和数据库健康。
- 对 SMTP/IMAP 做协议模糊测试和断电/崩溃恢复测试。

这是可运行的邮件服务器基础，但尚未宣称通过所有 RFC 可选扩展和客户端认证矩阵。
