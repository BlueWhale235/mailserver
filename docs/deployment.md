# 部署与上线

[返回文档索引](README.md)

## Docker

Docker Compose 文件面向源码仓库使用，会在本机从 `src` 构建各服务镜像。便携发布包不包含源码、Dockerfile 或 Compose 构建上下文。

先使用 CLI 生成 `mailserver.json`，再配置 `.env`。

MySQL：

先从 `.env.example` 复制 `.env`，确认数据库密码与 `MYSQL_PASSWORD` 一致。首次迁移前备份现有数据库和邮件存储；以下命令用于新安装或已确认备份的环境。

```bash
docker compose up -d mysql
docker compose --profile tools run --rm cli db update
docker compose --profile tools run --rm cli admin create admin --password 'replace-this-password'
docker compose up --build -d api smtp imap worker
```

新安装在首次 `db update` 时写入 1 GB 资源档位；已有数据库迁移后保留原设置。部署后可在“全局设置 → 资源与性能”选择运行环境的档位并查看 SMTP、IMAP、Worker 的实际应用状态。Compose 将相同数据库连接配置传给所有服务；不要在 `.env` 中覆盖缓存预算，否则会与档位冲突。详细档位与上限见[服务配置](configuration.md#资源与性能)。

上线前用 `/api/v1/health/ready` 检查数据库、迁移、邮件存储及安全资产状态，再验证 `/admin` 深层路由、登录与 CSRF、SMTP／IMAP 连接和所选投递路线。使用 Direct MX 时检查本机出站 TCP/25；使用中继时检查中继的双向 TCP/25、主服务器到中继的自定义端口，以及 MX、SPF、PTR。正式运行前还需完成清单中的 MySQL 实库并发验证和独立进程监控验收。

数据库类型、连接字符串、后台端口、邮件端口和 PEM 路径均可用环境变量覆盖。

## GitHub Actions 构建 Linux 单文件

将仓库放在 GitHub 后，`.github/workflows/release-linux.yml` 可由以下方式触发：

1. 在 Actions 中手动运行 **Build Linux binary**，输入 `v2.0.0` 这样的版本号。
2. 推送 `vMAJOR.MINOR.PATCH` 标签；预发布版本也可带 `-rc.1` 后缀。

工作流先安装 Node.js 24 与 .NET SDK 10.0.301，运行前端 Lint、测试、构建和后端测试，再调用 `scripts/build-linux-release.sh`。构建失败不会上传文件。成功后 Actions 的 artifact 名称包含版本号，内含：

```text
mailserver      # Linux x64 自包含单文件，含 Vue 管理后台
VERSION.txt     # 版本、架构、源码提交号及工作区状态
SHA256SUMS      # 二进制校验值
```

该工作流上传构建产物供下载，不会自动创建 GitHub Release 或部署服务器。当前仓库的 `origin` 是 Gitea；GitHub Actions 需将同一源码推送到 GitHub 仓库后才能运行。现有 Gitea 发布流程仍位于 `.gitea/workflows/release.yml`。

## 本地构建与版本

Linux 构建机需 Bash、Node.js 24、.NET SDK 10.0.301 和网络可访问的 npm／NuGet 包源。在仓库根目录运行：

```bash
bash scripts/build-linux-release.sh v2.0.0
```

脚本只接受 `vMAJOR.MINOR.PATCH[-预发布后缀]`，将 `2.0.0` 写入程序集版本；`mailserver --version` 会输出相同版本。生成目录为 `builds/releases/v2.0.0/linux-x64/`。如果该版本目录已存在，脚本会拒绝覆盖。`SHA256SUMS` 可在该目录用 `sha256sum -c SHA256SUMS` 验证。不生成 `tar.gz`。建议从干净的 Git 提交构建；若存在本地未提交修改，`VERSION.txt` 会标记 `Source state: modified`。

普通便携发布仍可使用 `scripts/publish-portable.sh` 或 Windows 上的 `scripts/publish-portable.ps1`；它们输出到 `builds/publish/<运行时>/`，不会给程序集指定发布版本。版本化 Linux 产物以 `scripts/build-linux-release.sh` 为准。

## 运行版本化单文件

把 `mailserver` 放到可写目录，赋予执行权限并执行 `./mailserver init` 创建独立配置、SQLite 数据库和初始管理员，再用 `./mailserver run all` 启动。首次初始化应设置正式域名、证书与需要的端口；使用 SQLite 时只运行一个 Worker。可用 `./mailserver doctor` 和 `/api/v1/health/ready` 检查状态。

Vue 管理后台的 HTML、JS、CSS 和字体已经嵌入二进制，不需要单独复制 `dist` 或 `wwwroot/admin`。启动时程序会在可执行文件旁释放 `wwwroot/admin`，因此目录需要写权限。上线后检查 `/admin/`、深层路由和 `/admin/assets/...`。

拆分部署只需启动 API 服务即可在同源 `/admin/` 访问后台；前端开发服务器仅用于本地开发。侧栏底部的“API 文档”在新标签页打开同源 `/swagger/`，仍需登录。运维中心的邮件查询仅覆盖现有邮件记录与原文，遵循原有回收站与清理期限；`operations.mail.read` 只开放元数据，预览和下载还需 `operations.mail.content.read`。

Linux 自包含发布不需要预装 .NET，但仍使用系统的 glibc、OpenSSL 和 ICU。Ubuntu 26.04 WSL2 实测需安装 `libicu78`；其它发行版安装对应的 ICU 运行库。配置、证书、邮件内容和数据库不会嵌入二进制，需要按环境独立保存并备份。

## 上线前

- 完成 MX、A/AAAA、PTR、SPF、DKIM、DMARC 配置。
- 使用受信任插件或隔离服务增加反垃圾与病毒扫描。
- 配置数据库、邮件内容和 Data Protection 密钥备份。
- 测试 Thunderbird、Outlook、Apple Mail 与 MailKit。
- 监控队列深度、磁盘、TLS 有效期、认证失败和数据库健康。
- 对 SMTP/IMAP 做协议模糊测试和断电/崩溃恢复测试。

这是可运行的邮件服务器基础，但尚未宣称通过所有 RFC 可选扩展和客户端认证矩阵。
