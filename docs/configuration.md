# 服务配置

[返回文档索引](README.md)

## 资源与性能

管理后台“全局设置 → 资源与性能”提供六个内存预设。点击预设会填充表单，所有数值仍可修改；偏离预设时显示“自定义（基于某档位）”，可恢复预设或撤销未保存修改。档位表示部署环境的规模，不是进程内存硬限制，也不保证相应吞吐。保存后 SMTP、IMAP、Worker 自动应用，无需重启；降低限制时已建立连接与正在投递的任务继续完成，新工作按新上限进入。新安装默认 1 GB；升级后若尚未选择档位，继续使用原有设置，页面显示“尚未选择档位”。

| 档位 | 缓存预算 | 单项上限 | 并行投递 | SMTP 连接 | IMAP 连接 |
|---|---:|---:|---:|---:|---:|
| 512 MB | 4 MiB | 512 KiB | 1 | 8 | 16 |
| 1 GB | 8 MiB | 1 MiB | 2 | 16 | 32 |
| 2 GB | 16 MiB | 2 MiB | 4 | 32 | 64 |
| 4 GB | 32 MiB | 4 MiB | 8 | 64 | 128 |
| 8 GB | 64 MiB | 8 MiB | 16 | 128 | 256 |
| 16 GB 以上 | 128 MiB | 16 MiB | 32 | 256 | 512 |

常用参数为缓存总预算、单项缓存上限、Worker 并行投递数、SMTP 与 IMAP 连接上限。高级参数为配置检查周期、强制刷新周期、缓存加载并发、超预算规则查询批次，预设默认值分别为 3 秒、60 秒、2、64。预算与限制均须为正整数，单项上限不得超过总预算，强制刷新周期不得短于检查周期；加载并发范围 1–32，查询批次范围 1–512。保存的完整参数在重启后恢复；仅提交档位 ID 的旧版 API 请求仍应用对应预设。

`run all` 共享表中预算；拆分部署将预算分配给 SMTP 50%、IMAP 25%、Worker 25%，API 不保留配置缓存。多副本按实例各自计算。页面展示在线实例的缓存估算占用／预算合计，并可查看各服务的实际限制、活动连接／投递数量及最后上报时间；过期上报明确标识，不显示内部版本或命中统计。`GET/PUT /api/v1/settings/resources` 分别需要 `settings.read`、`settings.manage`；`GET /api/v1/settings/resources/status` 需要 `settings.read`。

配置文件中显式写入的更低 `Smtp:MaximumConnections` 或 `Imap:MaximumConnections` 仍作为安全上限，因此页面的请求值与服务上报的实际值可能不同。资源参数不会改变邮件大小、附件限制、重试次数或租约时长。512 MB 档适合 SQLite 单进程；同机运行 MySQL 和全部拆分进程需要额外内存。状态一般每 30 秒上报，90 秒未上报显示“未收到上报”。

## 连接与登录保护

“邮件安全 → 连接与登录保护”可配置 SMTP 发信与 IMAP 登录失败封禁、每 IP 连接频率和同时连接数，以及记录容量和 TLS／登录前超时。默认启用：15 分钟内失败 5 次封禁 30 分钟；普通 SMTP 收信、SMTP 认证发信、IMAP 的每分钟连接额度分别为 120、60、60。SMTP 登录封禁不会阻断普通入站投递。共享出口 IP 的用户共用额度，配置时应考虑其正常并发。

防护记录只保存在各邮件服务进程的有界内存中，进程或副本之间不共享，重启后清空。后台保存会随配置检查自动应用，页面按进程显示应用状态；现有 `SmtpAbuse` 配置在尚未保存新防护设置时继续决定 SMTP 登录失败阈值。`GET/PUT /api/v1/security/connection-protection` 分别需要 `firewall.read`、`firewall.manage`；状态接口 `GET /api/v1/security/connection-protection/status` 需要读取权限。应用层限制可减少密码爆破及连接耗尽，但公网带宽耗尽需要网络入口防护。

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

MailServer 2.0 仅支持 `DirectMx`：本机查询收件方 MX 并通过 TCP/25 投递，支持无 MX 时 A/AAAA 回退、多 MX/IP 故障转移、STARTTLS、MTA-STS、DANE、TLS-RPT、DKIM 与退信。

部署前应确认公网 25 端口、PTR、SPF、DKIM、DMARC 和发信 IP 信誉。就绪检查失败会持续告警，但不会阻止域外发。旧配置含有 `Relay` 配置节时服务会拒绝启动；请重新生成 2.0 配置。

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
