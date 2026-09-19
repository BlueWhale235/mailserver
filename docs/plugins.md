# 插件开发与安装

[返回文档索引](README.md)

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
相对的 `Plugins:Path` 始终以服务器可执行文件所在目录为基准，而不是 systemd、
面板或终端启动服务时的工作目录。也可以将它配置成绝对路径。

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
