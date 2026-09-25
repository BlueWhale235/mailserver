# 快速开始与 CLI

[返回文档索引](README.md)

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
mailserver --version
mailserver init
mailserver doctor
mailserver config validate
mailserver db status
mailserver db update
mailserver admin create <name> --password <value>
mailserver admin password <name> --password <value>
mailserver domain add <domain>
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
