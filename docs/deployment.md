# 部署与上线

[返回文档索引](README.md)

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

先从 `.env.example` 复制 `.env`，设置 `DATABASE_PROVIDER=MySql`，并将 `MAIL_DATABASE_CONNECTION` 设为指向 `mysql:3306` 的连接字符串；其中的数据库密码须与 `MYSQL_PASSWORD` 一致。不要沿用示例中的 PostgreSQL 默认连接串。首次迁移前备份现有数据库和邮件存储；以下命令用于新安装或已确认备份的环境。

```bash
docker compose --profile mysql up -d mysql
docker compose --profile mysql --profile tools run --rm cli db update
docker compose --profile mysql --profile tools run --rm cli admin create admin --password 'replace-this-password'
docker compose --profile mysql up --build -d api smtp imap worker
```

上线前用 `/api/v1/health/ready` 检查数据库、迁移、邮件存储及安全资产状态，再验证 `/admin` 深层路由、登录与 CSRF、SMTP／IMAP 连接和 Direct MX 出站 TCP/25。正式运行前还需完成清单中的 MySQL 实库并发验证和独立进程监控验收。

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
