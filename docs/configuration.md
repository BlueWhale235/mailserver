# 服务配置

[返回文档索引](README.md)

## 资源与性能

管理后台“全局设置 → 资源与性能”提供七个内存预设。点击预设会填充表单，所有数值仍可修改；偏离预设时显示“自定义（基于某档位）”，可恢复预设或撤销未保存修改。档位表示部署环境的规模，不是进程内存硬限制，也不保证相应吞吐。保存后 SMTP、IMAP、Worker 自动应用，无需重启；降低限制时已建立连接与正在投递的任务继续完成，新工作按新上限进入。新安装默认 1 GB；升级后若尚未选择档位，继续使用原有设置，页面显示“尚未选择档位”。

| 档位 | 缓存预算 | 单项上限 | 并行投递 | SMTP 连接 | IMAP 连接 |
|---|---:|---:|---:|---:|---:|
| 512 MB | 4 MiB | 512 KiB | 1 | 8 | 16 |
| 1 GB | 8 MiB | 1 MiB | 2 | 16 | 32 |
| 2 GB | 16 MiB | 2 MiB | 4 | 32 | 64 |
| 4 GB | 32 MiB | 4 MiB | 8 | 64 | 128 |
| 8 GB | 64 MiB | 8 MiB | 16 | 128 | 256 |
| 16 GB | 128 MiB | 16 MiB | 32 | 256 | 512 |
| 32 GB | 256 MiB | 32 MiB | 64 | 512 | 1024 |

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

默认使用 `DirectMx`：本机查询收件方 MX 并通过 TCP/25 投递，支持无 MX 时 A/AAAA 回退、多 MX/IP 故障转移、STARTTLS、MTA-STS、DANE、TLS-RPT、DKIM 与退信。系统设置也支持配对一台独立中继，分别启用实时收信与发信。中继通过公网 TCP/25 与外部邮件服务器通信；主服务器主动连接中继的自定义通信端口，邮件、队列及 DKIM 私钥仍留在主服务器。

部署前应确认公网 25 端口、PTR、SPF、DKIM、DMARC 和发信 IP 信誉。使用中继时，25 端口检查针对中继服务器，中继主机名应配置为收信域名的 MX，SPF 需授权中继出口 IP。就绪检查失败会持续告警，但不会阻止域外发。旧配置中的 `Relay` 配置节仍不兼容新的配对机制，服务会拒绝启动。

### 单节点邮件中继

1. 在主服务器的“系统设置 → 邮件中继”保存中继地址（默认通信端口 `18443`）和主服务器可从中继访问的配对地址（默认配对端口 `18444`）。API 进程在创建有效配对邀请后监听该配对端口。生产环境应限制其防火墙来源。
2. 点击“生成配对命令”，把命令复制到中继服务器执行。命令不含密码；运行后按提示输入页面一次性显示的密码。邀请有效期为 10 分钟。
3. 中继服务器使用 `mailserver relay run --port 18443 --smtp-port 25 [--config relay.json]` 启动。中继配置文件可以为空；身份和绑定信息保存在其应用目录的 `security-assets/relay/`。配对同一主服务器可再次执行命令；更换主服务器前先执行 `mailserver relay reset`，保留原中继身份密钥。
4. 测试加密连接，按需要分别启用收信与发信。拆分部署时 API 进程负责配对，SMTP 和 Worker 进程分别维持收信与发信隧道；这些进程必须共享数据库和主服务器的 `security-assets/relay/identity.pem`。

中继不保存邮件正文。主服务器断线时中继对新来的邮件回复临时错误；发信任务留在主服务器重试。解除配对会在线撤销中继绑定并关闭隧道；若中继离线，请在中继服务器执行 `mailserver relay reset` 清除旧绑定。解除配对不会自动把已启用的发信任务改成直连，管理员可以明确关闭发信中继来恢复直连。中继和主服务器的 PEM 身份证书只保存在各自应用目录，不加入系统信任库。

收信容量在主服务器的“系统设置 → 邮件中继”配置并下发到中继，无需编辑中继节点的 `relay.json`。三项默认值分别为 32 条总连接、每个来源 IP 4 条连接和 120 秒无数据活动超时；总连接允许 1–200、单 IP 允许 1–总连接、空闲超时允许 1–3600 秒。小 VPS 可降低并发数。连接时立即应用设置，后续修改约 3 秒内在线更新；降低上限不会中断已有连接。中继离线时主服务器保存设置，重连后再次下发。超限、隧道不可用时新连接收到 `421`，由发件方重试；传输中的连接只在持续无数据时超时，邮件大小仍由主服务器的 `Smtp:MaximumMessageBytes` 控制。中继不会用本地磁盘缓冲邮件。

主服务器与中继节点需要运行相同的隧道协议版本；升级时请两端一并更新，旧版节点会拒绝新版本连接并保持临时不可用，不会以旧配置继续接收邮件。

若主服务器显示“连接中继超时”，先确认中继进程输出了 `Relay listening for paired root on 18443`，再从**主服务器**测试到系统设置中“中继公网主机名或 IP”的 TCP `18443` 连通性。中继成功连到主服务器的配对端口 `18444` 只证明了反向路径可用；正常隧道需要主服务器能访问中继通信端口。检查该主机名解析出的 IPv4/IPv6 地址、中继主机防火墙及云服务商入站规则，并确认中继 `relay run --port` 与系统设置端口一致。

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

## 邮件检索补建与外发连接复用

`MessageSearch:Enabled` 默认开启。升级后先运行 `mailserver db update`，Worker 会自动补建新邮件和历史邮件的预解析检索数据。每批最多 16 封，默认批间等待 1 秒；与大型 MIME 操作共用处理许可，每封邮件保存的检索文本总量最多 512 KiB（UTF-8）。缺失、解析失败、旧版本或不完整记录会回到原文搜索。失败补建在 10 分钟后重试，进程重启可继续；COPY/MOVE 复用原邮件的记录，最终删除原邮件时级联清理。

设置 `MessageSearch:Enabled=false` 可停止补建并恢复原文搜索；保留数据库表和已补建记录。该开关和补建批次、间隔由配置文件控制，修改后重启相关服务。

`MessageSearch:UseCompleteHeaders` 默认开启。解析版本 2 区分完整邮件头与不完整正文：正文超限时，主题和邮件头的未命中也可直接采用检索记录；邮件头本身超限、记录缺失或旧版本仍检查原文。`MessageSearch:CacheDecodedSearchBodies` 和 `UseAsciiSearchNormalization` 默认开启，分别避免一次搜索内重复解码正文，以及对纯 ASCII 文本使用与 Unicode 路径一致的大写转换；含非 ASCII 字符的文本继续采用原有规范化方式。分别关闭开关并重启可恢复对应路径；旧版本服务无法识别的新状态会安全回退原文。

`MessageSearch:UsePersistentSearchStreams` 默认关闭。开启后，可定位的原文搜索流由 MIME 解析器直接引用，避免先复制完整原文到内存；不可定位流沿用复制路径。小内存实测仍出现较大的 Swap 和延迟峰值，因此保留为实验开关。超长正文的解码和规范化仍可能产生较大字符串。

`SmtpConnectionReuse:Enabled` 默认关闭。开启后 Direct MX 按目标域、MX、端口、出口地址配置、HELO 和 TLS 策略隔离连接，并在每次尝试时重新解析目标地址；路由改变后使用新连接。每个目标最多保留一条空闲连接，全进程默认最多四条；默认空闲 30 秒、总寿命 300 秒或发送 50 封后关闭。复用前用 NOOP 检查连接，检查或发送失败后淘汰，不在连接池内自动重发。实时中继与 MTA-STS testing 模式仍按单次连接处理，testing 模式保留逐次握手的证书诊断。关闭开关并重启 Worker 可恢复原连接生命周期。

`EfficiencyDiagnostics:Enabled=true` 可在验收实例中每 10 秒记录进程工作集、峰值工作集、堆大小、累计分配量和各代 GC 次数。默认关闭；它只写日志，不改变邮件处理策略。

## 入站暂存与重操作预算

`Storage:PreparedInboundEnabled` 默认 `false`。开启后 SMTP DATA 在暂存时计算内容 hash，存储层对同卷文件采用原子移动，避免再写一遍完整原文。重复内容仍复用已有文件；Linux 先核对源文件与目标目录的设备编号；跨设备或不能确认设备时直接沿用校验和复制路径，不依赖 File.Move 的隐式跨卷复制。其他平台保留复制路径。同卷移动失败会重新打开暂存流，继续沿用复制路径。`Storage:InboundTemporaryPath` 默认空，开启后使用存储目录下的 `.incoming`；指定路径应位于同一存储卷。取消、拒收与数据库回滚保留原有清理责任。关闭开关并重启 SMTP 可恢复原暂存路径，既有邮件无需转换。

`Resources:MaximumHeavyOperations` 默认按资源档选择：512mb/1gb 为 1，其余为 2，范围 1–8。入站解析、APPEND 保存及解析、原文 BODY 回退、需要 MIME 解析的 FETCH 和检索补建共用这一预算；连接额度另行控制。前台等待最多 30 秒，等待队列有界且支持取消；后台资源不足时暂停。本机许可释放时直接通知等待者，不轮询等待。不应根据机器总内存直接放大活跃任务数量。

检索补建的持久游标保存在现有 `system_settings` 的 `internal.search.backfill.cursor.v2`，以 `(ReceivedAt, Id)` 推进。当前版本失败记录按重试时间优先处理；扫描结束后循环校验，防止旧版本和较早时间的新记录永久遗漏。新邮件提交后的进程内通知只加速空批等待，非空批仍至少间隔 1 秒。回退旧版无需删除游标或检索数据。

`Imap:NoDelay` 默认 `true`，接受 TCP 连接后禁用 Nagle 等待，避免较小的 TLS/IMAP 分段响应等待对端延迟 ACK。它不改变响应内容、连接额度或重操作额度。设置为 `false` 并重启 IMAP 可恢复原套接字行为。


## 实验性动态资源调节

`AdaptiveResources.Enabled` 默认 `false`，保持既有资源档位行为。启用后固定初始额度为入站16、外发32、重MIME4；`AutomaticEnabled=true` 才根据CPU、Linux cgroup内存余量、GC和数据库延迟调整至32/64/12上限。缓存固定32MiB，`PauseBackfill=true` 暂停检索补建。每秒输出脱敏资源日志，不依赖诊断API。

该配置用于突发实验，不是生产默认。当前内存准入采用每项256MiB保守预留，加512MiB余量；尚未实现可信的逐任务峰值学习。外发解析完成后释放重MIME额度，内存预留到内容释放才归还。后台控制器发出停止请求时，实验编排器必须停止新负载；控制器不自行终止服务。采样失败禁止扩容。

`SmtpConnectionReuse.MaximumIdleConnections` 默认仍为4，实验可设置到16，保持每目标最多1条空闲连接。动态并发及连接复用需单独开关；测试后恢复原配置。应用cgroup4GiB上限不包含独立MySQL或宿主操作系统。
