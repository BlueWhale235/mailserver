# Frontend Design Components

[返回文档索引](../README.md)

This admin UI now keeps common control patterns in reusable Razor components.

## AdminPageHeader

Use as the heading for every top-level administration page. It keeps the title, description, and right-side actions aligned consistently without category eyebrow text.

```razor
<AdminPageHeader Title="域名管理"
                 Description="管理邮件域名、DNS、SSL/TLS、账号与外发方式。"
                 CssClass="domain-list-heading">
    <Actions>
        <button class="btn btn-primary admin-btn" type="button">添加域名</button>
    </Actions>
</AdminPageHeader>
```

API:

| Parameter | Type | Description |
| --- | --- | --- |
| `Title` | `string` | Required page title rendered as the single page-level `h1`. |
| `Description` | `string?` | Optional supporting text below the title. |
| `Actions` | `RenderFragment?` | Optional right-side commands; the component owns the action layout container. |
| `CssClass` | `string?` | Extra class for page-specific spacing or layout adjustments. |

## SwitchField

Use for settings that enable or disable a feature. Prefer this over raw checkbox labels for configuration forms.

```razor
<SwitchField Label="启用域名"
             Description="停用后该域名不再接收或发送邮件。"
             @bind-Value="_isActive" />
```

API:

| Parameter | Type | Description |
| --- | --- | --- |
| `Value` | `bool` | Current on/off state. Supports `@bind-Value`. |
| `ValueChanged` | `EventCallback<bool>` | Fires when the switch changes. |
| `Label` | `string?` | Primary label text. |
| `Description` | `string?` | Optional helper text under the label. |
| `CssClass` | `string?` | Extra class for layout variants, such as `switch-card`. |
| `Disabled` | `bool` | Disables interaction. |

## ServerDataGrid

Use for searchable, paginated tables such as alias account selection, API keys, admin users, and delivery queue.

System and domain delivery queues must enable `MultiSelect`, provide `ItemKey`, and expose `SelectedIdsChanged`. This gives every row a checkbox and the header a current-page select-all checkbox.

```razor
<ServerDataGrid TItem="AccountView"
                LoadData="LoadAliasAccountsAsync"
                SelectedItem="_selectedAccount"
                SelectedItemChanged="AccountSelected"
                SearchPlaceholder="搜索账号地址"
                EmptyText="没有可用账号"
                ColumnCount="4">
    <HeaderTemplate>
        <th>选择</th>
        <th>账号</th>
        <th>状态</th>
        <th>配额</th>
    </HeaderTemplate>
    <RowTemplate Context="account">
        <td>@account.Address</td>
        <td>@account.IsEnabled</td>
        <td>@account.CreatedAt</td>
        <td>...</td>
    </RowTemplate>
</ServerDataGrid>
```

API:

| Parameter | Type | Description |
| --- | --- | --- |
| `LoadData` | `Func<string,int,int,Task<PagedResult<TItem>>>` | Loads one page. Arguments are search text, page, page size. |
| `HeaderTemplate` | `RenderFragment` | Table header cells only; the component owns the `<tr>`. |
| `RowTemplate` | `RenderFragment<TItem>` | Cells for each row. |
| `ToolbarTemplate` | `RenderFragment?` | Extra filters/actions beside the search box. |
| `SelectedItem` / `SelectedItemChanged` | `TItem?` / `EventCallback<TItem?>` | Single row selection. |
| `MultiSelect` | `bool` | Shows checkbox column and enables multiple selection. |
| `ItemKey` | `Func<TItem, Guid>?` | Required for `MultiSelect`; returns the selected row id. |
| `SelectedIds` / `SelectedIdsChanged` | `IReadOnlySet<Guid>` / `EventCallback<IReadOnlySet<Guid>>` | External batch actions can read selected ids here. |
| `ToggleOnRowClick` | `bool` | Toggles checkbox selection when clicking a row. |
| `SearchPlaceholder` | `string` | Search input placeholder. |
| `EmptyText` | `string` | Empty state text. |
| `PageSizeOptions` | `int[]` | Available page sizes. Defaults to `10,20,50`. |
| `InitialPageSize` | `int` | Initial page size. |
| `ColumnCount` | `int` | Number of data columns, excluding the optional checkbox column. |
| `ShowSearch` | `bool` | Hide search input when false. |
| `SearchOnInput` | `bool` | Defaults to `true`. Set to `false` when IME-friendly manual search is needed; call `ReloadFromFirstPageAsync()` from a search button or press Enter in the search box. |
| `CssClass` | `string?` | Extra class for page-level variants. |

Public methods via `@ref`:

| Method | Description |
| --- | --- |
| `RefreshAsync(bool firstPage = false)` | Reloads current page, or first page when requested. |
| `ReloadFromFirstPageAsync()` | Resets to page 1 and reloads. |

## PriorityTransferList

Use for left-right ordered selections, including outbound IP priority.

```razor
<PriorityTransferList TItem="string"
                      SelectedItems="selectedIps"
                      AvailableItems="availableIps"
                      SelectedItemsChanged="SaveSelectedIps"
                      KeySelector="ip => ip"
                      ItemLabel="ip => ip"
                      ItemDescription="DescribeIp"
                      SelectedTitle="发件 IP 列表"
                      AvailableTitle="所有 IP 列表"
                      AllowRemoveFromSelected="true" />
```

API:

| Parameter | Type | Description |
| --- | --- | --- |
| `SelectedItems` | `IReadOnlyList<TItem>` | Left-side ordered priority list. |
| `AvailableItems` | `IReadOnlyList<TItem>` | Right-side selectable list. |
| `SelectedItemsChanged` | `EventCallback<IReadOnlyList<TItem>>` | Emits the new ordered selected list after drag, add, remove, or reorder. |
| `KeySelector` | `Func<TItem,string>` | Stable key used for drag/reorder identity. |
| `ItemLabel` | `Func<TItem,string>` | Main row label. |
| `ItemDescription` | `Func<TItem,string>?` | Optional secondary row text. |
| `SelectedTitle` / `SelectedSubtitle` | `string` | Left lane heading. |
| `AvailableTitle` / `AvailableSubtitle` | `string` | Right lane heading. |
| `EmptySelectedText` / `EmptyAvailableText` | `string` | Empty lane text. |
| `AllowRemoveFromSelected` | `bool` | Shows the right-arrow remove action and allows dropping back to the right lane. |
| `AvailableAddsToTop` | `bool` | Adds/moves available items to the top instead of the end. |
| `AvailableIcon` | `string` | Bootstrap icon class for right-side rows. |
| `AvailableActionIcon` | `string` | Bootstrap icon class for the right-side action button. |
| `AvailableActionTitle` | `string` | Tooltip for the right-side action button. |
| `CssClass` | `string?` | Extra class for layout variants. |
| `SelectedFooterTemplate` | `RenderFragment?` | Fixed footer row, such as the system default fallback after IP priority. |

Rows reserve a separate action column. Labels and descriptions use a shrinkable text area with safe wrapping, so long IPv6 addresses cannot overlap move buttons.

## QuotaProgress

Use for account and domain capacity in tables or quota management panels. The bar changes from green to orange at 75% and red at 90%.

```razor
<QuotaProgress UsedBytes="account.UsedBytes"
               QuotaBytes="account.QuotaBytes"
               Compact="true"
               AriaLabel="账号配额使用率" />
```

API:

| Parameter | Type | Description |
| --- | --- | --- |
| `UsedBytes` | `long` | Current logical mailbox usage in bytes. |
| `QuotaBytes` | `long` | Capacity limit in bytes. |
| `Compact` | `bool` | Applies the stable table-cell width. |
| `AriaLabel` | `string` | Accessible progress bar name. |
| `CssClass` | `string?` | Optional layout-specific class. |

## SelectDropdown

Use the generic `SelectDropdown<TValue>` component for every single-choice menu. Native `<select>` elements are not used in the admin UI, so the closed state, option panel, focus ring, dark mode, and responsive behavior remain consistent across browsers.

```razor
<SelectDropdown TValue="string"
                @bind-Value="_status"
                Options="StatusOptions"
                AriaLabel="状态" />
```

API:

| Parameter | Type | Description |
| --- | --- | --- |
| `Value` / `ValueChanged` | `TValue` / `EventCallback<TValue>` | Current value and two-way binding callback. |
| `Options` | `IReadOnlyList<AdminSelectOption<TValue>>` | Values, labels, and optional disabled states. |
| `Placeholder` | `string` | Trigger text when nothing is selected. |
| `AriaLabel` | `string?` | Accessible trigger and listbox name. |
| `CssClass` | `string?` | Optional layout class. |
| `Disabled` | `bool` | Disables the trigger. |

## MultiSelectDropdown

Use the generic `MultiSelectDropdown<TItem, TValue>` component for multiple-choice menus. The panel has a top select-all checkbox for the currently filtered results. `取消选择` only appears after at least one value has been selected.

```razor
<MultiSelectDropdown TItem="MailDomain" TValue="Guid"
                     Items="_domains"
                     ValueSelector="domain => domain.Id"
                     TextSelector="domain => domain.Name"
                     SelectedValues="_domainIds"
                     SelectedValuesChanged="DomainSelectionChanged"
                     SelectedUnit="个域名"
                     SearchPlaceholder="筛选域名" />
```

## PermissionGrantEditor

Use for granting coarse permissions to administrators and API keys. The modal keeps selected permissions on the left and available permissions on the right. The available side uses `ServerDataGrid` with manual search, so typing with a Chinese IME does not trigger queries until Enter or the search button.

```razor
<PermissionGrantEditor Title="编辑管理员授权"
                       SubjectName="admin"
                       InitialGrants="currentGrants"
                       OnSave="SaveGrantsAsync"
                       OnCancel="CloseGrantEditor" />
```

API:

| Parameter | Type | Description |
| --- | --- | --- |
| `Title` | `string` | Modal title, such as `创建 API Key 授权`. |
| `SubjectName` | `string` | The administrator name or API key name shown under the title. |
| `InitialGrants` | `IReadOnlyList<PermissionGrantInput>` | Current grants. Full access `*` is mutually exclusive with detailed grants. |
| `OnSave` | `EventCallback<IReadOnlyList<PermissionGrantInput>>` | Emits normalized grants. Call the admin/API key grant save API here. |
| `OnCancel` | `EventCallback` | Closes the modal without saving. |

Layout rules:

| Area | Behavior |
| --- | --- |
| Left pane | Selected permissions, removable one by one. |
| Right pane | `ServerDataGrid<PermissionCatalogItem>` listing available permissions with checkbox rows. |
| Resource scope | `系统权限`, `全部域名`, or `指定域名`. Specified domains use `DomainMultiSelect`. |
| Empty save | Invalid; callers should keep the modal open and show the validation message. |

Permission codes:

`*`, `iam.manage`, `dashboard.read`, `domain.read`, `domain.manage`, `api-key.read`, `api-key.manage`, `admin.account.read`, `admin.account.manage`, `firewall.read`, `firewall.manage`, `queue.read`, `queue.manage`, `dns.read`, `dns.manage`, `settings.read`, `settings.manage`.

`*.manage` includes the matching `*.read` behavior. Old fine-grained permission strings are migration-only and must not be sent by the frontend.

## Permission API

Use these API endpoints when external UI code needs to manage permissions.

| Endpoint | Purpose |
| --- | --- |
| `GET /api/v1/permissions/catalog` | Returns permission groups and permission items. Requires `iam.manage`. |
| `GET /api/v1/admins/{id}/grants` | Returns grants for an administrator. Requires `iam.manage`. |
| `PUT /api/v1/admins/{id}/grants` | Replaces grants for an administrator. Requires `iam.manage`. |
| `GET /api/v1/admins` | Returns paged administrators. Requires `admin.account.read`. |
| `POST /api/v1/admins` | Creates an administrator and initial grants. Requires `admin.account.manage` and `iam.manage`. |
| `PATCH /api/v1/admins/{id}` | Updates administrator name, enabled state, or password. Requires `admin.account.manage`. |
| `DELETE /api/v1/admins/{id}` | Deletes an administrator. Requires `admin.account.manage`. |
| `GET /api/v1/api-keys/{id}/grants` | Returns grants for an API key. Requires `iam.manage`. |
| `PUT /api/v1/api-keys/{id}/grants` | Replaces grants for an API key. Requires `iam.manage`. |
| `POST /api/v1/api-keys` | Creates an API key and its initial grants. Requires `api-key.manage` and `iam.manage`. |
| `GET /api/v1/queues/{id}/raw` | Returns raw MIME text and an `.eml` filename for a delivery job. Requires `queue.read`. |
| `GET /api/v1/domains/{domainId}/queue` | Returns a paged queue filtered to the route domain. Requires `domain.read` for that domain. |
| `GET /api/v1/domains/{domainId}/queue/{id}/raw` | Returns raw MIME only when the job belongs to the route domain. Requires `domain.read`. |
| `POST /api/v1/domains/{domainId}/queue/{id}/retry` | Retries a job only when it belongs to the route domain. Requires `domain.manage`. |
| `DELETE /api/v1/domains/{domainId}/queue/{id}` | Deletes a job only when it belongs to the route domain. Requires `domain.manage`. |

Grant payload shape:

```json
{
  "grants": [
    { "permissionCode": "domain.read", "resourceType": "domain", "resourceKey": "00000000-0000-0000-0000-000000000000" },
    { "permissionCode": "firewall.manage", "resourceType": "", "resourceKey": "" }
  ]
}
```

Special values:

| Value | Meaning |
| --- | --- |
| `permissionCode: "*"` | Full access. Must not be combined with detailed grants. |
| `resourceType: "domain"` + `resourceKey: "*"` | All domains for that domain-scoped permission. |
| Empty `resourceType` and `resourceKey` | System-level permission. |

API key creation payload:

```json
{
  "name": "automation",
  "grants": [
    { "permissionCode": "domain.read", "resourceType": "domain", "resourceKey": "*" }
  ],
  "expiresAt": null
}
```

`scopes` is not part of the frontend API. Always send `grants`; creating a new administrator or API key with an empty grant list is invalid.

Administrator creation payload:

```json
{
  "userName": "operator",
  "password": "change-this-strong-password",
  "isEnabled": true,
  "grants": [
    { "permissionCode": "dashboard.read", "resourceType": "", "resourceKey": "" }
  ]
}
```

## Buttons and Toasts

Mutation and validation outcomes use the top-right `AdminToastService`; do not render transient success or error messages in page-level `<p>` elements. Toast DOM uses the project-specific `.admin-toast` class rather than Bootstrap's `.toast` class. Primary save/create/search/confirm actions use blue, ordinary actions use blue outlines, cancel uses gray outlines, and destructive actions use red. Panel save actions live in a bottom-right `.panel-actions` row; modal actions remain in `.modal-actions`.

Copy actions call `mailserverClipboard.copyText`. It uses the modern Clipboard API in secure contexts and a browser-side text selection fallback for HTTP deployments, so copied text reaches the user's local system clipboard in both cases.

## Plugin Configuration

`/admin/plugins` uses `ServerDataGrid` with four columns: plugin name, description, enabled status, and actions. The edit action opens a modal whose first control is always `SwitchField`.

All remaining controls are generated from the plugin's `plugin.json` settings schema. Boolean values use `SwitchField`, single choices use `SelectDropdown`, multiple choices use `MultiSelectDropdown`, and password values never echo the stored secret. Plugin mutations use `AdminToastService`.

## MessageTemplateEditor

`/admin/notification-templates` uses a tabbed HTML source editor for notification templates. Current tabs are `退信模板` and `错误模板`; each tab owns a subject input, an HTML source textarea, and a rounded preview card containing an iframe rendered with sample variables.

Available variables: `{recipient}`, `{reason}`, `{queueId}`, `{attempts}`. HTML rendering escapes variable values before insertion.

## Delivery Queue Message Headers

Queue detail dialogs lazy-load only RFC message headers after the `邮件头` tab is opened. The service stops reading at the first header/body separator and enforces a 256 KiB limit, so message bodies, inline images, and attachments never enter the Vue render tree. Copy operates on the header text only. Full `.eml` downloads use the authenticated streaming API endpoint, so attachments are streamed rather than serialized into JSON.

Deleting a queue task is a hard delete. Once the stored message has no remaining delivery, mailbox, or sent-copy references, its database record and content-store file are removed. Shared messages stay available until their final reference is deleted.

Queue status labels are localized as `待处理`, `处理中`, `已送达`, `延迟重试`, and `失败`. Domain queues use `domain.read` for viewing and `domain.manage` for retry/delete; system queues continue to use `queue.read` and `queue.manage`.
