# 自研 SMTP / IMAP 邮件系统异常场景测试清单

[返回文档索引](../README.md)

本文档用于测试自研邮件系统在 SMTP、IMAP、MIME、存储、同步、客户端兼容性、安全与反滥用等方面的边界行为。

---

## 一、SMTP / 投递层场景

### 1. 收件人部分成功、部分失败

同一封邮件中存在多个 `RCPT TO`，其中一部分地址有效，一部分地址无效。

```smtp
MAIL FROM:<alice@example.com>
RCPT TO:<bob@example.com>
RCPT TO:<notfound@example.com>
RCPT TO:<carol@example.com>
DATA
Subject: test

Hello
.
```

测试点：

* 是否只投递给有效用户
* 是否正确返回部分失败
* 是否生成 DSN
* 日志中是否能准确记录成功和失败的收件人

---

### 2. DATA 阶段中途断线

客户端发送了：

```smtp
DATA
Subject: test

Hello
```

但还没有发送结束符：

```smtp
.
```

连接就断开。

预期行为：

* 该邮件不应入库
* 不应投递给任何收件人
* 不应产生半封邮件
* 连接资源应被正确释放

---

### 3. Dot-stuffing

SMTP 正文中，如果某一行以 `.` 开头，客户端会进行转义。

客户端发送：

```text
..hello
```

服务器存储时应还原为：

```text
.hello
```

建议测试：

```text
.
..
...
.test
```

注意：

* 单独一行 `.` 是 DATA 结束符
* `..` 应被还原为 `.`
* `...` 应被还原为 `..`
* `.test` 在传输时通常应为 `..test`

---

### 4. 空发件人地址

退信常用空 envelope sender。

```smtp
MAIL FROM:<>
RCPT TO:<user@example.com>
DATA
Subject: delivery failure

...
.
```

预期行为：

* `MAIL FROM:<>` 是合法的
* 系统不应简单拒绝
* 不应对空发件人继续生成退信，避免退信风暴

---

### 5. Header From 和 Envelope From 不一致

例如：

```smtp
MAIL FROM:<bounce@mailer.com>
RCPT TO:<user@example.com>
DATA
From: CEO <ceo@company.com>
Subject: hello

...
.
```

测试点：

* Envelope sender 用于投递、退信、SPF 等
* Header From 用于展示、DMARC 等
* 系统不能混淆二者

---

### 6. 重复 Message-ID

两封不同邮件使用相同的：

```text
Message-ID: <same-id@example.com>
```

测试点：

* 系统是否错误去重
* IMAP 中是否仍能显示两封邮件
* 搜索和线程聚合是否出现异常

---

### 7. 没有 Message-ID

部分客户端或脚本可能不生成 `Message-ID`。

测试点：

* 是拒收、自动生成，还是原样保存
* 如果自动生成，是否保证唯一
* 线程聚合是否受到影响

---

### 8. Header 超长或折行异常

合法折行：

```text
Subject: hello
 world
```

非法或危险格式：

```text
Subject: hello
world: injected
```

测试点：

* 是否支持 header continuation
* 是否防止 header 注入
* 是否限制 header 总长度
* 是否能处理超长 Subject

---

### 9. 非 ASCII 邮箱地址

例如：

```smtp
MAIL FROM:<测试@例子.公司>
RCPT TO:<用户@example.com>
```

测试点：

* 是否支持 SMTPUTF8
* 不支持时是否优雅拒绝
* 支持时是否能正确存储、搜索、展示和退信

---

### 10. 大附件接近限制

例如系统限制 25MB，测试：

* 24.9MB
* 25MB
* 25.1MB

注意：

* Base64 编码后体积会膨胀
* 原始 18MB 附件编码后可能接近 24MB
* 限制应针对传输大小还是解码后大小，需要明确

---

### 11. 一封邮件有大量收件人

例如 500 个 `RCPT TO`。

测试点：

* 每封邮件最大收件人数限制
* 部分失败处理
* 队列压力
* 日志膨胀
* 防止滥发

---

### 12. HELO / EHLO 名称异常

```smtp
EHLO localhost
EHLO [127.0.0.1]
EHLO very-long-invalid-name.example.example.example
```

测试点：

* 不应因为 EHLO 名称异常而崩溃
* 是否记录可疑 EHLO
* 是否过度信任 EHLO 名称

---

### 13. RSET 后事务状态是否完全清空

```smtp
MAIL FROM:<a@example.com>
RCPT TO:<b@example.com>
RSET
DATA
```

预期行为：

* `RSET` 后应清空当前 envelope
* 后续 `DATA` 应失败，因为没有有效收件人

---

### 14. NOOP / VRFY / EXPN 的处理

```smtp
NOOP
VRFY alice
EXPN staff
```

测试点：

* `NOOP` 应正常返回
* `VRFY` / `EXPN` 即使不支持，也应稳定返回
* 不应泄露用户枚举信息

---

### 15. STARTTLS 后必须重新 EHLO

```smtp
EHLO client
STARTTLS
EHLO client
MAIL FROM:<a@example.com>
```

测试点：

* TLS 握手后能力集可能变化
* 客户端通常应重新 EHLO
* 未重新 EHLO 时系统行为是否明确

---

### 16. STARTTLS 降级攻击场景

场景：

* 服务端支持 STARTTLS
* 中间人删除能力声明
* 客户端误以为服务端不支持 TLS

测试点：

* 是否支持 MTA-STS
* 是否支持 TLS-RPT
* 是否支持 DANE
* 是否能记录明文降级风险

---

### 17. AUTH 状态错乱

测试顺序：

```smtp
AUTH PLAIN ...
AUTH PLAIN ...
MAIL FROM:<a@example.com>
AUTH PLAIN ...
```

测试点：

* 已认证后再次 AUTH
* `MAIL FROM` 后再 AUTH
* AUTH 失败次数限制
* 是否有暴力破解防护

---

### 18. AUTH 后发件人冒用

用户 `alice@example.com` 登录后尝试：

```smtp
MAIL FROM:<ceo@example.com>
```

测试点：

* 是否允许代发
* 是否有授权规则
* 是否有审计记录
* 是否会影响 DKIM / SPF / DMARC

---

### 19. MAIL FROM 参数

```smtp
MAIL FROM:<a@example.com> SIZE=12345 BODY=8BITMIME SMTPUTF8
```

测试点：

* 是否正确解析地址和参数
* 不支持的参数如何处理
* 是否错误地把参数当成邮箱地址的一部分

---

### 20. RCPT TO 参数

```smtp
RCPT TO:<b@example.com> NOTIFY=SUCCESS,FAILURE ORCPT=rfc822;b@example.com
```

测试点：

* DSN 相关参数是否支持
* 不支持时是否优雅拒绝或忽略
* 是否影响收件人地址解析

---

### 21. 8BITMIME 内容

```smtp
MAIL FROM:<a@example.com> BODY=8BITMIME
```

正文中包含裸 UTF-8 或 8-bit 字节。

测试点：

* 存储是否保持原样
* 转发是否保持原样
* IMAP FETCH 是否保持原始字节
* 不支持 8BITMIME 时是否拒绝

---

### 22. 裸 CR、裸 LF、CRLF 混用

```text
line1\n
line2\r
line3\r\n
```

测试点：

* 是否规范化换行
* 是否原样保存
* 是否错误判断 DATA 结束
* 是否导致 DKIM 验证失败

---

### 23. DATA 结束符前有空白

合法结束符必须是单独一行：

```text
.
```

以下不应被识别为结束符：

```text
. 
 .
	.
```

测试点：

* 是否严格识别 DATA 结束符
* 是否防止正文被提前截断

---

### 24. 多个 MAIL FROM 未 RSET

```smtp
MAIL FROM:<a@example.com>
MAIL FROM:<b@example.com>
```

预期行为：

* 第二个 `MAIL FROM` 应失败
* 除非已经 `RSET` 或上一封邮件事务已完成

---

### 25. QUIT 在事务中出现

```smtp
MAIL FROM:<a@example.com>
RCPT TO:<b@example.com>
QUIT
```

预期行为：

* 未完成邮件应丢弃
* 连接应干净关闭
* 不应产生半封邮件

---

### 26. 命令大小写混合

```smtp
eHlO client
mAiL fRoM:<a@example.com>
rCpT tO:<b@example.com>
```

预期行为：

* SMTP 命令大小写不敏感
* 参数是否大小写敏感需按语义区分

---

### 27. 命令参数前后异常空格

```smtp
MAIL    FROM:<a@example.com>
RCPT TO:   <b@example.com>
```

测试点：

* parser 是否过于脆弱
* 是否能兼容常见客户端
* 是否能拒绝明显非法格式而不崩溃

---

### 28. 地址注释和特殊格式

```smtp
MAIL FROM:<"weird name"@example.com>
RCPT TO:<user+tag@example.com>
RCPT TO:<user@[192.0.2.1]>
```

测试点：

* quoted local-part
* plus addressing
* address literal
* parser 是否过度简化

---

### 29. 空邮件正文

```smtp
DATA

.
```

预期行为：

* 空正文邮件是合法的
* 不应因为 body 为空而拒绝

---

### 30. 只有 header、没有 body

```text
From: a@example.com
To: b@example.com
Subject: no body

```

预期行为：

* 合法邮件
* 展示和搜索不应出错

---

### 31. 没有 header，只有 body

```text
hello world
.
```

测试点：

* 系统策略是接受还是拒绝
* parser 不应崩溃
* 展示层是否有 fallback

---

### 32. 重复 header 字段

```text
Subject: one
Subject: two
From: a@example.com
From: b@example.com
```

测试点：

* 展示使用哪个字段
* 搜索索引如何处理
* 安全判断使用哪个字段
* 是否标记异常邮件

---

### 33. Received 链异常

邮件中已有很多 `Received` 头，甚至伪造内部 IP。

测试点：

* 系统是否只信任自己新增的那一跳
* 是否盲信用户提供的 `Received`
* 是否限制 Received 数量防止循环

---

### 34. 超多小邮件突发

场景：

* 一秒内 1000 封 1KB 邮件
* 与一封 100MB 邮件完全不同

测试点：

* 队列吞吐
* 索引压力
* 通知压力
* IMAP EXISTS 推送
* 连接数限制

---

### 35. 慢速 SMTP 客户端

客户端每 30 秒发送一个字节。

测试点：

* 连接超时
* 防慢速攻击
* 每连接资源限制
* 半开连接清理

---

### 36. 灰名单重试

如果支持 greylisting：

* 第一次返回临时失败
* 客户端稍后重试
* 系统接受邮件

测试点：

* 4xx 是否进入延迟队列
* 是否错误生成永久退信
* 重试策略是否合理

---

### 37. MX 多目标投递

外域投递时，目标域有多个 MX。

测试点：

* 主 MX 连接失败时是否尝试下一个
* 4xx 和 5xx 处理是否不同
* MX 优先级是否正确
* 是否有重试队列

---

### 38. 开放中继测试

未认证外部用户尝试：

```smtp
MAIL FROM:<x@external.com>
RCPT TO:<y@external.com>
```

预期行为：

* 绝不能允许系统作为开放中继
* 应返回拒绝
* 应记录滥用尝试

---

### 39. 本地域名伪造

外部未认证用户发送：

```smtp
MAIL FROM:<admin@yourdomain.com>
RCPT TO:<localuser@yourdomain.com>
```

测试点：

* 是否标记为外部来源
* 是否经过 SPF / DKIM / DMARC 检查

---

### 40. 同一邮件重复投递

上游 SMTP 超时，不确定你是否已经返回 `250 OK`，于是重试投递同一封邮件。

测试点：

* 是否会重复收两封
* 是否使用队列 ID、Message-ID、内容 hash 去重
* 去重是否会误伤合法重复邮件

---

## 二、IMAP 协议场景

### 41. 同一邮箱多客户端并发操作

场景：

* 客户端 A 删除邮件
* 客户端 B 移动邮件
* 客户端 C 正在 FETCH 同一封邮件

测试点：

* UID 是否稳定
* flags 是否正确合并
* sequence number 是否正确变化
* 是否出现错删、错取

---

### 42. Sequence number 和 UID 混淆

```imap
A1 FETCH 1 BODY[]
A2 UID FETCH 1 BODY[]
```

注意：

* 第一个 `1` 是 sequence number
* 第二个 `1` 是 UID

测试点：

* 系统是否混淆两种编号
* 删除或 EXPUNGE 后行为是否正确

---

### 43. EXPUNGE 导致序号变化

邮箱中有三封邮件：

```text
1, 2, 3
```

第 1 封被 expunge 后：

```text
1, 2
```

原来的 2 变成新的 1。

测试点：

* 客户端批量操作时是否错乱
* 服务端 untagged EXPUNGE 是否正确
* sequence number 是否实时更新

---

### 44. UIDVALIDITY 变化

场景：

* 邮箱被重建
* 数据迁移
* 索引重建
* UID 分配体系变化

测试点：

* 是否更新 UIDVALIDITY
* 客户端缓存是否被正确失效
* 是否避免邮件错乱

---

### 45. 邮件移动后的 UID 行为

Inbox 中 UID 123 移到 Archive。

测试点：

* 目标文件夹是否分配新的 UID
* UID 是否只在单个 mailbox 内唯一
* MOVE / COPY 后 UID 映射是否正确返回

---

### 46. Flags 并发冲突

客户端 A：

```imap
STORE 5 +FLAGS (\Seen)
```

客户端 B 同时：

```imap
STORE 5 +FLAGS (\Flagged)
```

预期结果：

```text
\Seen \Flagged
```

测试点：

* flags 是否合并
* 是否后写覆盖前写
* 是否有事务隔离问题

---

### 47. 自定义 flags

```imap
STORE 1 +FLAGS ($Forwarded $Junk MyCustomFlag)
```

测试点：

* 是否支持自定义 flags
* 不支持时是否明确返回
* 支持时是否持久化
* `PERMANENTFLAGS` 是否正确声明

---

### 48. 文件夹名包含特殊字符

例如：

```text
项目/客户A
Archive.2026
"Quotes"
空 格
emoji-📩
```

测试命令：

```imap
LIST "" "*"
CREATE "a/b"
CREATE "a.b"
CREATE "中文文件夹"
```

测试点：

* hierarchy delimiter
* UTF-7 / UTF-8 mailbox name
* 引号和转义
* 空格和 emoji

---

### 49. INBOX 大小写

以下是否都指向同一邮箱：

```text
INBOX
Inbox
inbox
```

测试点：

* INBOX 在 IMAP 中有特殊语义
* 普通 mailbox 是否大小写敏感需要明确

---

### 50. FETCH BODY 部分读取

```imap
FETCH 1 BODY[]<0.1024>
FETCH 1 BODY[]<1024.1024>
```

测试点：

* offset 是字节，不是字符
* 含中文、emoji、附件时是否准确
* 换行规范化是否影响 offset
* 大附件下载是否稳定

---

### 51. 只取 header

```imap
FETCH 1 BODY.PEEK[HEADER]
FETCH 1 BODY.PEEK[HEADER.FIELDS (FROM TO SUBJECT DATE)]
```

测试点：

* header 字段大小写
* 多行折叠
* 多值字段
* 是否准确过滤指定字段

---

### 52. BODY.PEEK 不应标记已读

```imap
FETCH 1 BODY.PEEK[]
```

不应设置 `\Seen`。

而：

```imap
FETCH 1 BODY[]
```

通常会设置 `\Seen`。

测试点：

* `BODY.PEEK` 和 `BODY` 行为是否区分
* 并发 FETCH 是否重复触发 flag 更新

---

### 53. SEARCH 日期理解

```imap
SEARCH SINCE 1-Jul-2026
SEARCH BEFORE 1-Jul-2026
SEARCH ON 1-Jul-2026
```

测试点：

* 使用 Date header 还是 INTERNALDATE
* 日期边界是否正确
* 时区是否正确
* 日本时间和 UTC 跨日场景

---

### 54. SORT / THREAD 不支持时的行为

```imap
SORT
THREAD
```

测试点：

* 不支持时返回 `BAD` 或 `NO`
* 不应断开连接
* CAPABILITY 是否准确声明

---

### 55. IDLE 长连接

```imap
IDLE
```

测试点：

* 新邮件到达是否推送 `EXISTS`
* flag 变化是否推送
* 连接超时如何处理
* 客户端断线是否释放资源

---

### 56. IMAP literal 数据

```imap
A1 APPEND INBOX {12}
Hello world!
```

服务器应先返回：

```imap
+ Ready for literal data
```

测试点：

* literal 长度是否按字节计算
* off-by-one
* 换行处理
* 超时处理
* 内存限制

---

### 57. 未登录状态下发命令

```imap
A1 SELECT INBOX
A2 FETCH 1 BODY[]
```

预期行为：

* 返回未认证错误
* 不应泄露邮箱信息

---

### 58. 已登录但未 SELECT 邮箱时发 FETCH

```imap
A1 LOGIN user pass
A2 FETCH 1 BODY[]
```

预期行为：

* `FETCH` 需要 selected mailbox 状态
* 应返回错误

---

### 59. EXAMINE 只读邮箱

```imap
A1 EXAMINE INBOX
A2 STORE 1 +FLAGS (\Seen)
```

预期行为：

* `EXAMINE` 后 mailbox 是只读
* 不应允许修改 flags

---

### 60. CLOSE 与 EXPUNGE 差异

测试点：

* `CLOSE` 会关闭当前 mailbox
* 会清除 `\Deleted` 邮件
* 但不逐条返回 `EXPUNGE`
* 客户端状态应保持正确

---

### 61. UNSELECT 支持与否

```imap
A1 UNSELECT
```

测试点：

* 支持时回到 authenticated state
* 不支持时返回清晰错误
* CAPABILITY 是否准确

---

### 62. NOOP 触发异步更新

```imap
A1 NOOP
```

服务器可能返回：

```imap
* 23 EXISTS
* 5 EXPUNGE
A1 OK NOOP completed
```

测试点：

* 是否能返回 untagged responses
* tagged response 顺序是否正确
* 客户端状态是否同步

---

### 63. 命令 tag 特殊格式

```imap
abc123 SELECT INBOX
. FETCH 1 FLAGS
中文 LOGIN a b
```

测试点：

* tag 合法性
* 非法 tag 是否稳定拒绝
* 是否影响协议解析

---

### 64. 同一连接 pipelining

客户端连续发送：

```imap
A1 SELECT INBOX
A2 FETCH 1:* FLAGS
A3 NOOP
```

测试点：

* 是否能按顺序处理
* tagged response 是否乱序
* untagged response 是否正确插入

---

### 65. 大范围 FETCH

```imap
A1 FETCH 1:* BODY[]
```

邮箱中有 10 万封邮件。

测试点：

* 是否流式输出
* 是否分页或限制
* 是否一次性加载导致 OOM
* 客户端断开后是否停止处理

---

### 66. FETCH 不存在的序号

```imap
A1 FETCH 999999 FLAGS
```

预期行为：

* 通常返回空结果并 OK
* 不应返回 BAD
* 不应崩溃

---

### 67. UID FETCH 不存在的 UID

```imap
A1 UID FETCH 999999 FLAGS
```

预期行为：

* 返回空集合
* 不应报协议错误

---

### 68. 空邮箱下的命令

```imap
A1 SELECT INBOX
A2 FETCH 1 FLAGS
A3 SEARCH ALL
A4 EXPUNGE
```

测试点：

* 空邮箱下所有命令应稳定
* SEARCH 应返回空结果
* EXPUNGE 不应出错

---

### 69. SEARCH CHARSET

```imap
A1 SEARCH CHARSET UTF-8 SUBJECT "测试"
A2 SEARCH CHARSET ISO-8859-1 SUBJECT "café"
```

测试点：

* 支持哪些 charset
* 不支持时是否明确返回
* 搜索索引编码是否一致

---

### 70. SEARCH 大小写和 Unicode 归一化

测试内容：

```text
é
é
Ｅｘａｍｐｌｅ
example
```

测试点：

* Unicode normalization
* 全角半角
* 大小写折叠
* 搜索和展示是否一致

---

### 71. SEARCH HEADER 多行折叠

```imap
A1 SEARCH HEADER Subject "hello world"
```

邮件中：

```text
Subject: hello
 world
```

预期行为：

* 应匹配折叠后的 header 值

---

### 72. BODYSTRUCTURE 精确性

复杂 MIME 下客户端依赖 `BODYSTRUCTURE` 决定正文和附件。

测试点：

* MIME 参数
* NIL
* 编码
* 大小
* 行数
* 嵌套结构
* 附件识别

---

### 73. RFC822.SIZE 是原始字节数

测试点：

* 如果入库存储时规范化换行，`RFC822.SIZE` 是否仍准确
* FETCH 返回的完整邮件字节数是否与 `RFC822.SIZE` 一致
* DKIM 验证是否受影响

---

### 74. INTERNALDATE 保持稳定

测试点：

* 邮件复制后 INTERNALDATE 是否变化
* 邮件移动后是否变化
* 重新索引后是否变化
* 客户端排序是否受影响

---

### 75. APPEND 指定 flags 和日期

```imap
A1 APPEND INBOX (\Seen \Flagged) "08-Jul-2026 12:34:56 +0900" {12}
Hello world!
```

测试点：

* flags 是否保存
* internal date 是否保存
* literal 长度是否正确
* UIDNEXT 是否更新

---

### 76. APPEND 到不存在文件夹

```imap
A1 APPEND "NotExists" {12}
Hello world!
```

预期行为：

* 返回类似 `NO [TRYCREATE]`
* 客户端可据此创建 mailbox 后重试

---

### 77. APPEND literal 长度过大

```imap
A1 APPEND INBOX {999999999}
```

测试点：

* 不应直接分配巨大内存
* 应有大小限制
* 应能提前拒绝

---

### 78. Literal+ 非同步 literal

如果服务端支持 LITERAL+，客户端可能不等 `+` 就发送 literal。

测试点：

* 不支持时不要在 CAPABILITY 中声明
* 支持时解析状态机是否正确
* 大 literal 是否限流

---

### 79. quoted string 转义

```imap
A1 CREATE "folder \"quoted\" name"
A2 CREATE "folder \\ slash"
```

测试点：

* 引号转义
* 反斜杠转义
* 空格
* 特殊字符

---

### 80. mailbox wildcard

```imap
A1 LIST "" "%"
A2 LIST "" "*"
A3 LIST "Projects/" "%"
```

测试点：

* `%` 只匹配一层
* `*` 匹配多层
* hierarchy delimiter 是否正确

---

### 81. LSUB 订阅列表

```imap
A1 SUBSCRIBE "Archive"
A2 LSUB "" "*"
A3 UNSUBSCRIBE "Archive"
```

测试点：

* 订阅状态是否持久化
* LIST 和 LSUB 是否区分
* Thunderbird 等客户端兼容性

---

### 82. 删除当前选中的 mailbox

场景：

* 连接 A SELECT `Projects`
* 连接 B 删除 `Projects`
* 连接 A 继续 `NOOP` / `FETCH` / `CLOSE`

测试点：

* 当前连接如何响应
* 是否产生悬空 mailbox
* 是否通知客户端状态变化

---

### 83. 重命名父文件夹

```imap
A1 RENAME "Projects" "OldProjects"
```

如果存在：

```text
Projects/Alpha
Projects/Beta
```

测试点：

* 子文件夹是否一起移动
* delimiter 是否正确
* 客户端缓存是否失效

---

### 84. 删除有子文件夹的邮箱

```imap
A1 DELETE "Projects"
```

如果存在：

```text
Projects/Alpha
```

测试点：

* 是拒绝删除
* 还是只删除可选中状态
* 是否保留层级节点
* 行为需一致

---

### 85. INBOX 重命名

```imap
A1 RENAME INBOX OldInbox
```

测试点：

* INBOX 有特殊规则
* 是否允许
* 旧邮件如何处理
* 新 INBOX 是否自动存在

---

### 86. STATUS 不应隐式 SELECT

```imap
A1 STATUS INBOX (MESSAGES UNSEEN UIDNEXT UIDVALIDITY)
```

预期行为：

* 查询 mailbox 状态
* 不应改变当前 selected mailbox

---

## 三、MIME / 邮件内容层场景

### 87. MIME boundary 出现在正文中

boundary：

```text
----abc
```

正文中也出现相似内容：

```text
------abc
```

测试点：

* parser 是否误判 boundary
* 是否严格按 MIME boundary 规则识别

---

### 88. 嵌套 multipart

结构：

```text
multipart/mixed
  multipart/alternative
    text/plain
    text/html
  application/pdf
```

测试点：

* 正文选择
* 附件识别
* 预览生成
* IMAP BODYSTRUCTURE

---

### 89. 缺失或错误的 Content-Type

缺失：

```text
Content-Type:
```

错误：

```text
Content-Type: text/html; charset="unknown-charset"
```

测试点：

* 是否 fallback 到 text/plain
* charset 无法识别时是否安全处理
* parser 不应崩溃

---

### 90. 非法 Base64

测试内容：

* padding 错误
* 含非法字符
* 长度不完整
* 行长异常

测试点：

* 是拒收、保存原文，还是标记解析失败
* IMAP FETCH 是否还能返回原始邮件
* 附件下载是否安全失败

---

### 91. 同名附件

一封邮件中两个附件都叫：

```text
invoice.pdf
```

测试点：

* 下载时是否能区分
* 预览是否覆盖
* Web UI 是否显示重复名称
* 存储层是否避免覆盖文件

---

### 92. 特殊编码文件名

RFC 2231：

```text
Content-Disposition: attachment;
 filename*=UTF-8''%E6%8A%A5%E5%91%8A.pdf
```

RFC 2047：

```text
filename="=?UTF-8?B?...?="
```

测试点：

* 文件名解码
* fallback
* 下载 header
* 安全清理

---

### 93. 纯 HTML 邮件，没有 text/plain

测试点：

* 搜索索引是否能提取文本
* 通知摘要是否正常
* HTML sanitizer 是否安全

---

### 94. 只有 text/plain，没有 HTML

测试点：

* 展示是否正常
* 搜索是否正常

---

### 95. multipart 缺少结束 boundary

```text
Content-Type: multipart/mixed; boundary=abc

--abc
Content-Type: text/plain

hello
```

缺少：

```text
--abc--
```

测试点：

* parser 是否接受
* 是否标记邮件损坏
* 是否避免无限等待或读取越界

---

### 96. boundary 前后空格

```text
--abc 
--abc--
```

测试点：

* 是否严格处理 boundary 行
* 是否兼容脏数据
* 行尾空格如何处理

---

### 97. boundary 名包含特殊字符

```text
Content-Type: multipart/mixed; boundary="----=_NextPart_(abc)+123"
```

测试点：

* 参数解析是否支持引号
* 是否支持特殊字符
* 是否只支持简单字母数字

---

### 98. header 编码跨多段

```text
Subject: =?UTF-8?B?5rWL?= =?UTF-8?B?6K+V?=
```

测试点：

* encoded-word 是否拼接
* 中间空格处理是否正确
* 搜索和展示是否一致

---

### 99. encoded-word 错误用于参数

```text
Content-Disposition: attachment; filename="=?UTF-8?B?...?="
```

测试点：

* 虽然不完全规范，但真实世界常见
* 是否兼容
* 是否有 fallback

---

### 100. Content-Transfer-Encoding 与内容不符

```text
Content-Transfer-Encoding: base64

hello world
```

或声明 quoted-printable 但内容非法。

测试点：

* 解码失败是否影响原文保存
* FETCH 原始邮件是否完整
* 展示层是否安全失败

---

### 101. quoted-printable 软换行

```text
hello=
world
```

应解码为：

```text
helloworld
```

测试点：

* 软换行
* 行尾空格
* 非法 `=XX`
* 多字节字符被拆开

---

### 102. 空 multipart part

```text
--abc
Content-Type: text/plain

--abc
Content-Type: application/pdf

...
--abc--
```

测试点：

* 空正文 part 是否导致预览异常
* 附件识别是否正确

---

### 103. message/rfc822 嵌套邮件附件

```text
Content-Type: message/rfc822
```

测试点：

* 嵌套邮件是否作为附件展示
* 是否索引嵌套内容
* 转发是否保持结构
* BODYSTRUCTURE 是否准确

---

### 104. multipart/related

HTML 正文引用内嵌图片：

```html
<img src="cid:image001.png@abc">
```

测试点：

* CID 解析
* 内嵌图片是否重复显示为附件
* 远程图片和内嵌图片是否区分
* 隐私策略是否正确

---

### 105. calendar invite

```text
Content-Type: text/calendar; method=REQUEST
```

测试点：

* 会议邀请展示
* 时区
* 重复邀请
* 取消邀请
* 更新序列号
* RSVP 状态

---

### 106. S/MIME 或 PGP 邮件

```text
multipart/signed
application/pkcs7-mime
application/pgp-encrypted
```

测试点：

* 即使不解密，也应能保存
* 不应破坏结构
* 转发时应保持原样
* 附件和签名识别是否正确

---

### 107. 极深 MIME 嵌套

场景：

* multipart 嵌套 100 层

测试点：

* 递归深度限制
* 防止栈溢出
* BODYSTRUCTURE 是否限制输出

---

### 108. 极多 MIME part

场景：

* 一封邮件 5000 个小 part

测试点：

* 解析性能
* BODYSTRUCTURE 生成
* Web UI 展示
* 搜索索引
* 内存占用

---

### 109. 文本部分超长单行

场景：

* 一行 10MB，没有换行

测试点：

* preview 生成
* 搜索高亮
* IMAP partial FETCH
* Web UI 渲染

---

### 110. 错误 charset 名

```text
Content-Type: text/plain; charset=gb2312
Content-Type: text/plain; charset=GBK
Content-Type: text/plain; charset=x-unknown
```

测试点：

* charset alias
* fallback
* 替换字符
* 搜索索引与展示是否一致

---

### 111. 双重编码

```text
&amp;#20320;&amp;#22909;
```

测试点：

* HTML entity 解码策略
* 是否重复解码导致安全问题
* 展示和搜索是否一致

---

## 四、存储、同步与恢复场景

### 112. 投递成功但索引失败

场景：

* 邮件已成功入库
* 全文索引失败

测试点：

* IMAP 是否仍能看到邮件
* 搜索是否稍后补偿
* 用户是否会丢信
* 是否有重试任务

---

### 113. 附件存储成功但元数据写入失败

场景：

* 对象存储已有附件
* 数据库事务失败

测试点：

* 是否产生孤儿对象
* 是否有清理机制
* 是否可以安全重试

---

### 114. 元数据成功但正文 blob 丢失

场景：

* 数据库中有邮件记录
* 正文 blob 不存在

测试点：

* IMAP FETCH 如何返回错误
* 是否影响整个 mailbox
* 是否告警
* 是否可修复

---

### 115. UIDNEXT 回滚

场景：

* 服务重启
* 主从切换
* UID 分配器状态丢失

测试点：

* UIDNEXT 不能回退
* UID 必须单调唯一
* 客户端同步不能错乱

---

### 116. 主从复制延迟

场景：

* SMTP 写主库
* IMAP 读从库
* 用户收到通知但 IMAP 查不到

测试点：

* 读写一致性策略
* 是否强制读主
* 是否延迟通知
* 是否支持 session stickiness

---

### 117. 跨节点并发分配 UID

场景：

* 两个投递节点同时给同一 mailbox 投递邮件

测试点：

* UID 是否单调唯一
* 是否有分布式锁或原子计数器
* UIDNEXT 是否准确

---

### 118. 时钟回拨

场景：

* NTP 将系统时间调整回过去

测试点：

* INTERNALDATE
* 队列重试
* 证书校验
* 日志排序
* 去重窗口

---

### 119. 磁盘满

场景：

* SMTP DATA 接收中途磁盘满

预期行为：

* 应返回临时失败 `4xx`
* 不能返回成功后丢信
* 半成品数据应清理

---

### 120. 病毒扫描超时

测试策略：

* SMTP 阶段等待扫描
* 先收下再隔离
* 扫描失败后延迟处理

测试点：

* 超时时返回 4xx 还是接受
* 是否阻塞投递
* 是否有隔离区
* 是否有重试和告警

---

### 121. 邮件规则执行失败

场景：

* 用户规则：收到发票自动移动到 Bills
* 规则执行失败

预期行为：

* 邮件仍应保留在 Inbox 或安全位置
* 不能因为规则失败丢信
* 应记录错误并可重试

---

### 122. 通知发送失败

场景：

* 新邮件已经入库
* push 通知失败

测试点：

* 不应影响邮件可见性
* 是否有补偿任务
* 是否避免重复通知

---

### 123. 迁移旧邮箱

测试点：

* 是否保留原 Date
* 是否保留 INTERNALDATE
* 是否保留 flags
* 是否保留 folder
* 是否保留 UID
* 如果不保留，客户端是否会重新下载所有邮件

---

## 五、客户端兼容性场景

### 124. Outlook 高频同步

Outlook 可能频繁执行：

```imap
UID FETCH
BODY.PEEK[HEADER.FIELDS]
IDLE
```

测试点：

* 短时间大量重复同步
* 锁竞争
* FETCH 性能
* IDLE 重连

---

### 125. Apple Mail 部分下载

Apple Mail 常分段拉取大邮件和附件。

测试点：

* partial FETCH offset 是否稳定
* 大附件断点续拉
* 网络切换后重试
* BODYSTRUCTURE 是否准确

---

### 126. Thunderbird 文件夹订阅

Thunderbird 对以下内容较敏感：

* `LSUB`
* `LIST`
* 特殊文件夹
* folder subscription

测试点：

* Sent、Trash、Drafts、Junk 命名
* SPECIAL-USE flags
* 订阅状态持久化

---

### 127. 手机网络切换

场景：

* IMAP IDLE 中从 Wi-Fi 切到 5G
* TCP 连接半死不活

测试点：

* 服务端是否释放资源
* 客户端重连后状态是否一致
* IDLE 超时是否合理

---

### 128. 客户端重复 APPEND 已发送邮件

场景：

* SMTP 发送后系统自动保存 Sent
* 客户端又通过 IMAP `APPEND Sent` 保存一份

测试点：

* 是否出现双份已发送邮件
* 是否需要去重
* 去重是否安全

---

### 129. 草稿反复保存

客户端可能每隔几秒：

* 删除旧草稿
* APPEND 新草稿

测试点：

* Drafts 是否产生大量残留
* UID 是否快速膨胀
* 同步是否产生风暴
* 是否能合并草稿版本

---

### 130. 垃圾箱删除语义

不同客户端删除邮件方式不同：

* `STORE \Deleted`
* `MOVE Trash`
* `EXPUNGE`
* 直接删除

测试点：

* 用户体验是否一致
* Trash 是否正确
* 恢复邮件是否可行
* 永久删除是否明确

---

### 131. 特殊用途邮箱

客户端可能期待：

```text
\Sent
\Trash
\Drafts
\Junk
\Archive
```

测试点：

* 是否支持 IMAP SPECIAL-USE
* `LIST` 是否返回正确 flags
* 客户端是否能自动识别特殊文件夹

---

## 六、安全、反滥用与隐私场景

### 132. Header 注入

用户输入显示名：

```text
Alice
Bcc: victim@example.com
```

测试点：

* 是否直接拼接进 header
* 是否过滤 CRLF
* 是否可能注入 Bcc、Cc、Reply-To 等字段

---

### 133. 附件名路径穿越

附件名：

```text
../../../../etc/passwd
```

或：

```text
C:\Windows\system32\drivers\etc\hosts
```

测试点：

* 不能按原路径写入文件系统
* 应清理文件名
* 应避免覆盖已有文件

---

### 134. 压缩炸弹

场景：

* 小 zip 解压后巨大
* 多层嵌套压缩包

测试点：

* 解压后大小限制
* 解压层数限制
* CPU / 内存限制
* 扫描服务隔离

---

### 135. HTML 邮件危险内容

```html
<script>alert(1)</script>
<img src="http://tracker.example.com/open.png">
<a href="https://evil.example">https://real.example</a>
```

测试点：

* XSS 防护
* 跟踪像素
* 链接显示和真实 href 不一致
* HTML sanitizer

---

### 136. 邮件循环

场景：

* A 自动回复 B
* B 自动回复 A
* 退信触发退信
* 转发规则互相转发

测试点：

* `Auto-Submitted`
* `Precedence`
* 空 envelope sender
* 重复 Message-ID
* 循环次数限制

---

### 137. Unicode 混淆域名

例如：

```text
раураl.com
paypal.com
```

第一个可能使用西里尔字母。

测试点：

* 展示层是否提示风险
* 是否显示 punycode
* 钓鱼识别是否考虑 homoglyph

---

### 138. HTML 中 CSS 注入

```html
<style>
body { display:none }
</style>
```

测试点：

* CSS 是否能覆盖 Webmail UI
* 是否隐藏真实链接
* 是否绕过 sanitizer
* 是否允许外部字体或背景图

---

### 139. SVG 附件预览

SVG 可能包含：

* script
* 外链资源
* 嵌套 HTML
* 动画

测试点：

* 是否直接内联展示 SVG
* 是否沙箱
* 是否转为安全图片格式

---

### 140. PDF 预览漏洞隔离

测试点：

* PDF 预览服务是否沙箱化
* 是否限制网络访问
* 是否限制文件系统访问
* 是否限制 CPU 和内存
* 是否隔离用户数据

---

### 141. 外链图片隐私

HTML 邮件中：

```html
<img src="https://tracker.example/open?id=user123">
```

测试点：

* 是否默认阻止远程图片
* 是否通过代理加载
* 是否泄露用户 IP
* 是否泄露阅读时间
* 是否按发件人信任级别处理

---

### 142. DKIM 签名被系统改坏

场景：

* 入库时改写 header
* 规范化换行
* 删除 trailing spaces
* 修改 body

测试点：

* 是否破坏 DKIM
* 是否在修改前验证并保存认证结果
* 是否保留原始邮件字节

---

### 143. ARC / SPF / DKIM / DMARC 结果保存

测试点：

* 是否保存认证结果
* 是否用于 UI 展示
* 是否用于反垃圾判断
* 是否用于审计
* 是否在转发时处理 ARC

---

### 144. 退订头

```text
List-Unsubscribe:
List-Unsubscribe-Post:
```

测试点：

* 是否解析
* 是否展示一键退订
* 是否区分 mailto 和 https
* 是否防止伪造恶意退订链接

---

### 145. 自动回复泄露隐私

场景：

* 外出自动回复发给垃圾邮件
* 自动回复发给邮件列表
* 自动回复发给机器人

测试点：

* `Auto-Submitted`
* `List-Id`
* `Precedence: bulk`
* 是否限制每个发件人的自动回复频率

---

### 146. 邮件列表 Reply-To 陷阱

```text
Reply-To: list@example.com
```

测试点：

* 回复和回复全部的收件人计算
* 是否误发给全员
* UI 是否提示邮件列表

---

## 七、优先级最高的自动化测试建议

如果时间有限，建议优先自动化以下场景。

### SMTP 高优先级

1. `DATA` 中途断线
2. Dot-stuffing
3. `MAIL FROM:<>` 空发件人
4. 多收件人部分失败
5. `RSET` 清空事务状态
6. STARTTLS 后未重新 EHLO
7. AUTH 后冒用 envelope sender
8. CRLF / LF / CR 混用
9. 开放中继测试
10. 磁盘满时不能返回成功

### IMAP 高优先级

1. sequence number / UID 区分
2. EXPUNGE 后序号变化
3. BODY.PEEK 不标记已读
4. literal APPEND
5. EXAMINE 后禁止 STORE
6. pipelining 响应顺序
7. STATUS 不改变 selected mailbox
8. APPEND flags + internal date
9. UIDNEXT 重启后不回滚
10. 空邮箱命令稳定性

### MIME / 安全高优先级

1. 嵌套 multipart
2. MIME 缺失结束 boundary
3. 非法 Base64
4. 同名附件
5. Header 注入
6. 附件路径穿越
7. HTML XSS
8. 外链图片隐私
9. 压缩炸弹
10. DKIM 签名保护

---

## 八、推荐测试维度矩阵

| 测试维度   | 重点问题                                      |
| ------ | ----------------------------------------- |
| 协议状态机  | 命令顺序、事务清理、认证状态、selected mailbox           |
| 字节级正确性 | CRLF、literal 长度、partial FETCH、RFC822.SIZE |
| 并发一致性  | flags 合并、UID 分配、多客户端操作                    |
| 数据持久性  | 投递成功后不能丢信、索引失败可补偿                         |
| 客户端兼容性 | Outlook、Apple Mail、Thunderbird、移动端        |
| 安全     | 注入、XSS、路径穿越、开放中继、压缩炸弹                     |
| 隐私     | 远程图片、自动回复、邮件列表、认证结果                       |
| 可恢复性   | 磁盘满、主从延迟、时钟回拨、迁移旧邮箱                       |

---

## 九、测试设计建议

建议将测试分成四层：

1. **协议单元测试**

   * SMTP parser
   * IMAP parser
   * MIME parser
   * address parser

2. **状态机测试**

   * SMTP transaction state
   * IMAP authentication / selected state
   * STARTTLS / AUTH state
   * IDLE state

3. **集成测试**

   * SMTP 投递后 IMAP 可见
   * APPEND 后 FETCH 正确
   * MOVE / COPY / EXPUNGE 后 UID 正确
   * 多客户端并发一致性

4. **故障注入测试**

   * 磁盘满
   * 数据库失败
   * 对象存储失败
   * 索引失败
   * 扫描服务超时
   * 主从延迟
   * 网络中断

---

## 十、最容易被忽略但最致命的问题

以下问题尤其值得重视：

1. **返回 `250 OK` 后又丢信**

   * 这是邮件系统里最严重的问题之一。

2. **UIDNEXT 回滚**

   * 会导致客户端同步灾难。

3. **BODY.PEEK 标记已读**

   * 会让用户误以为邮件被阅读。

4. **开放中继**

   * 会让系统迅速被滥用并进入黑名单。

5. **附件路径穿越**

   * 可能导致服务器文件被覆盖或泄露。

6. **HTML 邮件 XSS**

   * Webmail 场景下风险极高。

7. **STARTTLS / AUTH 状态机错误**

   * 可能导致明文认证或权限绕过。

8. **partial FETCH offset 错误**

   * 大附件和移动客户端会频繁出问题。

9. **并发 flags 覆盖**

   * 多客户端用户会遇到状态丢失。

10. **未保留原始邮件字节**

    * 会影响 DKIM、IMAP SIZE、转发、取证和兼容性。
