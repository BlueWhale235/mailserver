# 退信模板 HTML 规则

消息模板包含永久失败、重试耗尽和超限退信。超限退信使用 `quotaExceededSubject` 与 `quotaExceededBody` 设置，默认提示发件人通过其他方式手动联系收件人，请其清理邮箱或增加容量后重新发送。旧配置缺失这两个字段时使用默认值；旧客户端更新未提供字段时保留已有值。

本地邮箱或域超限会返回 `552 5.2.2` 并回滚整个邮件事务，连接和认证状态保留。仅已认证且发件地址获授权的提交会额外尝试向信封发件人发送超限通知；匿名外部来信由发件方服务器处理拒收结果。通知独立提交，使用空信封发件人，不绕过配额、不递归退信；通知失败不会改变原来的拒收结果。原事务所有收件人均未成功投递，本地通知的 `{queueId}` 为“未入队”，`{attempts}` 为 `1`。

外发投递在 `5.2.2` 永久失败或 `4.2.2` 重试耗尽时使用超限模板。单封邮件大小超限 `5.3.4` 使用原来的失败模板。预览接口接受可选 `templateKind: "quota"`，为超限模板提供容量不足的示例原因。

退信正文只接受完整的 XHTML 片段。标签必须闭合（例如 `<br />`），文本中的 `&` 应写为 `&amp;`。此规则同时用于保存校验、服务端预览和实际退信渲染。

允许的标签：`section`、`div`、`p`、`h1`–`h3`、`strong`、`b`、`em`、`i`、`u`、`pre`、`code`、`br`、`ul`、`ol`、`li`、`blockquote`、`table`、`thead`、`tbody`、`tr`、`th`、`td`、`span`、`a`、`hr`。

所有标签仅允许 `style` 属性，且仅支持以下 CSS 属性：`font-family`、`font-size`、`font-weight`、`line-height`、`color`、`background`、`background-color`、`white-space`、`border`、`border-radius`、`padding`、`margin`、`text-align`、`width`、`max-width`。样式值只允许字母、数字、空格及 `# . , % -`，不接受 CSS 函数、转义或 URL。

`a` 标签还允许 `href`，协议限定为 `http`、`https` 或 `mailto`。不接受图片、外部资源、脚本、事件属性、命名空间属性、注释、文档类型声明及处理指令。

占位符 `{recipient}`、`{reason}`、`{queueId}`、`{attempts}` 在渲染时替换；HTML 正文中的值会编码。已保存的旧版模板若不符合当前规则，实际退信渲染时会作为纯文本显示，避免执行不安全标记；管理员应在页面修订并重新保存。
