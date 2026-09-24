# 退信模板 HTML 规则

退信正文只接受完整的 XHTML 片段。标签必须闭合（例如 `<br />`），文本中的 `&` 应写为 `&amp;`。此规则同时用于保存校验、服务端预览和实际退信渲染。

允许的标签：`section`、`div`、`p`、`h1`–`h3`、`strong`、`b`、`em`、`i`、`u`、`pre`、`code`、`br`、`ul`、`ol`、`li`、`blockquote`、`table`、`thead`、`tbody`、`tr`、`th`、`td`、`span`、`a`、`hr`。

所有标签仅允许 `style` 属性，且仅支持以下 CSS 属性：`font-family`、`font-size`、`font-weight`、`line-height`、`color`、`background`、`background-color`、`white-space`、`border`、`border-radius`、`padding`、`margin`、`text-align`、`width`、`max-width`。样式值只允许字母、数字、空格及 `# . , % -`，不接受 CSS 函数、转义或 URL。

`a` 标签还允许 `href`，协议限定为 `http`、`https` 或 `mailto`。不接受图片、外部资源、脚本、事件属性、命名空间属性、注释、文档类型声明及处理指令。

占位符 `{recipient}`、`{reason}`、`{queueId}`、`{attempts}` 在渲染时替换；HTML 正文中的值会编码。已保存的旧版模板若不符合当前规则，实际退信渲染时会作为纯文本显示，避免执行不安全标记；管理员应在页面修订并重新保存。
