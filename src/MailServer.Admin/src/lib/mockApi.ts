import { inspectFirewallInput } from './firewallInput'
import type { DomainCertificateInspection } from './domainCertificate'

interface MockDomain {
  id: string
  name: string
  mailHostName: string
  isActive: boolean
  dnsStatus: 'PendingConfiguration' | 'Checking' | 'Healthy' | 'Unhealthy'
  dnsCheckedAt?: string
  hasCertificate: boolean
  certificateNotAfter?: string
  accountCount: number
  quotaBytes: number
  usedBytes: number
  createdAt: string
}

interface MockDomainCertificate {
  domainId: string
  source: 'ManagedUpload' | 'ExternalPath'
  certificatePath: string
  privateKeyPath: string
  subject: string
  issuer: string
  subjectAlternativeNames: string
  thumbprint: string
  notBefore: string
  notAfter: string
  updatedAt: string
}

interface MockDomainAccount {
  id: string
  address: string
  isEnabled: boolean
  quotaBytes: number
  usedBytes: number
  createdAt: string
  aliasCount: number
}

interface MockDomainAlias {
  id: string
  source: string
  targetAccountId: string | null
  targetAddress: string | null
  isEnabled: boolean
  legacyDestinationAddress: string | null
  mode: 'SendReceive' | 'ReceiveOnly'
  canReceive: boolean
  canSend: boolean
}

interface MockRuleConditionView {
  id: string
  field: string
  operator: string
  values: string[]
  position: number
}

interface MockRuleActionView {
  id: string
  kind: string
  values: string[]
  position: number
}

interface MockDomainRule {
  id: string
  domainId: string
  name: string
  direction: 'Incoming' | 'Outgoing'
  matchMode: 'All' | 'Any'
  targetMailboxName: string
  isEnabled: boolean
  priority: number
  conditions: MockRuleConditionView[]
  actions: MockRuleActionView[]
}

type MockQuarantineStatus = 'PendingReview' | 'Released' | 'Deleted'

interface MockQuarantineItem {
  id: string
  domainId: string
  messageId: string
  sender: string
  recipients: string[]
  remoteAddress: string
  pluginId: string | null
  pluginName: string | null
  reason: string
  status: MockQuarantineStatus
  quarantinedAt: string
  reviewedBy: string | null
  reviewedAt: string | null
  reviewNote: string | null
  size: number
  internetMessageId: string | null
}

interface MockDomainAuditEntry {
  id: string
  actor: string
  action: string
  resource: string
  detail: string | null
  createdAt: string
}

interface MockDnsCheck {
  domainId: string
  status: 'PendingConfiguration' | 'Checking' | 'Healthy' | 'Unhealthy'
  validation: null | {
    domain: string
    items: Array<{ kind: string; name: string; expected: string; actual: string[]; status: string }>
    warnings: string[]
  }
  lastError: string | null
  checkedAt: string | null
}

interface MockDeliveryJob {
  id: string
  sender: string
  recipient: string
  status: 'Pending' | 'Processing' | 'Delivered' | 'Deferred' | 'Failed'
  attempts: number
  nextAttemptAt: string
  lastError?: string
  createdAt: string
  updatedAt: string
}

interface MockDeliveryAttempt {
  id: string
  attemptNumber: number
  succeeded: boolean
  permanentFailure: boolean
  stage: string
  mxHost?: string
  remoteAddress?: string
  smtpStatusCode?: string
  enhancedStatusCode?: string
  tlsProtocol?: string
  tlsCipherSuite?: string
  durationMilliseconds: number
  failureCategory?: string
  nextRetryAt?: string
  response?: string
  sessionSummary?: string
  createdAt: string
}

type MockTlsReportStatus = 'Pending' | 'Retrying' | 'Failed' | 'Completed'

interface MockTlsReportDelivery {
  id: string
  policyDomain: string
  reportUri: string
  periodStart: string
  periodEnd: string
  status: MockTlsReportStatus
  attempts: number
  nextAttemptAt: string
  completedAt?: string
  lastError?: string
  reportJson: string
}

interface MockPermissionGrant {
  permissionCode: string
  resourceType: string
  resourceKey: string
}

interface MockAdmin {
  id: string
  userName: string
  isEnabled: boolean
  createdAt: string
  isPrimary: boolean
}

interface MockApiKey {
  id: string
  name: string
  prefix: string
  createdBy: string
  createdAt: string
  lastUsedAt?: string
  expiresAt?: string
  revokedAt?: string
}

interface MockScheduledTask {
  id: string
  type: string
  name: string
  isSystem: boolean
  isEnabled: boolean
  intervalMinutes: number
  timeoutSeconds: number
  batchSize: number
  maximumAttempts: number
  status: 'Idle' | 'Running' | 'Paused' | 'Failed'
  nextRunAt: string
  lastRunAt?: string
}

interface MockTaskExecution {
  id: string
  scheduledTaskId: string
  status: 'Running' | 'Succeeded' | 'Failed' | 'Cancelled'
  startedAt: string
  completedAt?: string
  processedCount: number
  releasedBytes: number
  attempt: number
  error?: string
}

type MockCleanupResourceType = 'Domain' | 'Account' | 'Mailbox' | 'MailboxMessage' | 'ContentFile'
type MockCleanupQueueStatus = 'Pending' | 'Failed'

interface MockRecycleBinItem {
  queueId: string
  resourceType: MockCleanupResourceType
  resourceId: string | null
  displayName: string
  estimatedBytes: number
  deletedAt: string
  purgeAfter: string
  status: MockCleanupQueueStatus
}

type MockAuditCategory = 'Create' | 'Update' | 'Delete' | 'BackgroundTask' | 'Security' | 'Plugin' | 'Other'
type MockAuditStatus = 'Running' | 'Succeeded' | 'Failed' | 'Warning'

interface MockAuditEntry {
  id: string
  number: string
  content: string
  category: MockAuditCategory
  status: MockAuditStatus
  actor: string
  action: string
  resource: string
  detail?: string
  remoteAddress?: string
  requestId?: string
  relatedId?: string
  durationMilliseconds?: number
  pluginId?: string
  pluginName?: string
  createdAt: string
  changes: Array<{ field: string; oldValue?: string; newValue?: string }>
  rawJson: string
}

type MockPluginSettingType = 'Text' | 'Integer' | 'Boolean' | 'Select' | 'Multiselect' | 'Password'

interface MockPluginSettingDefinition {
  key: string
  label: string
  type: MockPluginSettingType
  description?: string
  required?: boolean
  minimum?: number
  maximum?: number
  placeholder?: string
  options: Array<{ label: string; value: string }>
}

interface MockPlugin {
  id: string
  displayName: string
  description: string
  version: string
  system: boolean
  enabled: boolean
  isHostActive: boolean
  loadError?: string
  order: number
  effectiveOrder: number
}

interface MockPluginConfiguration {
  definitions: MockPluginSettingDefinition[]
  values: Array<{ key: string; value: string | number | boolean | string[] | null; hasSecret: boolean }>
}

const previewStorageKey = 'mailserver-admin-preview'
let previewMode = import.meta.env.DEV && (
  new globalThis.URLSearchParams(globalThis.location.search).get('preview') === '1'
  || globalThis.sessionStorage.getItem(previewStorageKey) === '1')
const now = new Date('2026-09-18T12:30:00Z')
let previewSession = true
let previewFirewall = [{ id: 'e548a192-5f60-4798-af3f-fae386ca52b1', cidr: '192.0.2.0/24', note: '演示拦截规则', isEnabled: true }]
const previewPermissionCatalog = [
  { key: 'global', name: '全局访问', permissions: [
    { code: '*', name: '全部权限', description: '拥有后台和 API 的全部操作权限。', groupKey: 'global', supportsDomainScope: false },
  ] },
  { key: 'access', name: '身份与访问', permissions: [
    { code: 'iam.manage', name: '授权管理', description: '管理管理员和 API Key 的权限授权。', groupKey: 'access', supportsDomainScope: false },
    { code: 'api-key.read', name: 'API Key 只读', description: '查看 API Key 列表、状态和授权。', groupKey: 'access', supportsDomainScope: false },
    { code: 'api-key.manage', name: 'API Key 管理', description: '创建、刷新、授权和删除 API Key。', groupKey: 'access', supportsDomainScope: false },
    { code: 'admin.account.read', name: '管理员只读', description: '查看后台管理员账号列表。', groupKey: 'access', supportsDomainScope: false },
    { code: 'admin.account.manage', name: '管理员管理', description: '创建、编辑、授权和删除后台管理员账号。', groupKey: 'access', supportsDomainScope: false },
  ] },
  { key: 'domains', name: '域与邮箱', permissions: [
    { code: 'domain.read', name: '域名只读', description: '查看授权范围内的域名、邮箱和投递信息。', groupKey: 'domains', supportsDomainScope: true },
    { code: 'domain.manage', name: '域名管理', description: '管理授权范围内的域名配置、邮箱、规则与证书。', groupKey: 'domains', supportsDomainScope: true },
    { code: 'domain.quarantine.read', name: '审核与隔离只读', description: '查看授权域的隔离邮件与审核状态。', groupKey: 'domains', supportsDomainScope: true },
    { code: 'domain.quarantine.manage', name: '审核与隔离管理', description: '释放或删除授权域的隔离邮件。', groupKey: 'domains', supportsDomainScope: true },
  ] },
  { key: 'system', name: '系统功能', permissions: [
    { code: 'dashboard.read', name: '概览只读', description: '查看后台概览。', groupKey: 'system', supportsDomainScope: false },
    { code: 'audit.read', name: '审计日志只读', description: '查看与导出全局审计日志。', groupKey: 'system', supportsDomainScope: false },
    { code: 'firewall.read', name: '防火墙只读', description: '查看防火墙规则。', groupKey: 'system', supportsDomainScope: false },
    { code: 'firewall.manage', name: '防火墙管理', description: '创建、编辑和删除防火墙规则。', groupKey: 'system', supportsDomainScope: false },
    { code: 'queue.read', name: '投递队列只读', description: '查看外发投递队列与诊断。', groupKey: 'system', supportsDomainScope: false },
    { code: 'queue.manage', name: '投递队列管理', description: '重试或删除投递任务。', groupKey: 'system', supportsDomainScope: false },
    { code: 'settings.read', name: '系统设置只读', description: '查看系统设置和通知模板。', groupKey: 'system', supportsDomainScope: false },
    { code: 'settings.manage', name: '系统设置管理', description: '修改系统设置和通知模板。', groupKey: 'system', supportsDomainScope: false },
    { code: 'dns.read', name: 'DNS 诊断只读', description: '执行并查看 DNS 诊断。', groupKey: 'system', supportsDomainScope: false },
    { code: 'dns.manage', name: 'DNS 诊断管理', description: '管理 DNS 诊断配置。', groupKey: 'system', supportsDomainScope: false },
  ] },
  { key: 'operations', name: '任务中心', permissions: [
    { code: 'task.read', name: '任务中心只读', description: '查看计划任务、执行历史和回收站。', groupKey: 'operations', supportsDomainScope: false },
    { code: 'task.manage', name: '任务中心管理', description: '配置和执行任务，管理回收项。', groupKey: 'operations', supportsDomainScope: false },
  ] },
]
let previewAdmins: MockAdmin[] = [
  { id: 'd25d8438-3d40-4696-83da-101d142b2aa1', userName: 'preview-admin', isEnabled: true, createdAt: '2026-01-03T03:20:00Z', isPrimary: true },
  { id: '7c631894-68c7-4f18-bd67-31b38dc73002', userName: 'mail-operator', isEnabled: true, createdAt: '2026-06-18T09:10:00Z', isPrimary: false },
  { id: '3f4a48c5-bb90-485c-82c3-48b9dca3e903', userName: 'audit-reviewer', isEnabled: false, createdAt: '2026-08-07T12:40:00Z', isPrimary: false },
]
const previewAdminGrants: Record<string, MockPermissionGrant[]> = {
  'd25d8438-3d40-4696-83da-101d142b2aa1': [{ permissionCode: '*', resourceType: '', resourceKey: '' }],
  '7c631894-68c7-4f18-bd67-31b38dc73002': [
    { permissionCode: 'domain.manage', resourceType: 'domain', resourceKey: '*' },
    { permissionCode: 'queue.manage', resourceType: '', resourceKey: '' },
    { permissionCode: 'dashboard.read', resourceType: '', resourceKey: '' },
  ],
  '3f4a48c5-bb90-485c-82c3-48b9dca3e903': [
    { permissionCode: 'audit.read', resourceType: '', resourceKey: '' },
    { permissionCode: 'domain.read', resourceType: 'domain', resourceKey: '9e8fef40-335f-4d7a-a6c5-7379bb9aa401' },
  ],
}
let previewApiKeys: MockApiKey[] = [
  {
    id: 'de90c87a-bc83-437a-86c4-61c1c28ccb01',
    name: 'metrics-exporter',
    prefix: 'msk_live_7JK2',
    createdBy: 'preview-admin',
    createdAt: '2026-05-11T03:00:00Z',
    lastUsedAt: '2026-09-17T13:54:00Z',
  },
  {
    id: 'de90c87a-bc83-437a-86c4-61c1c28ccb02',
    name: 'backup-agent',
    prefix: 'msk_live_4PA9',
    createdBy: 'preview-admin',
    createdAt: '2026-07-26T10:30:00Z',
    lastUsedAt: '2026-09-16T20:18:00Z',
    expiresAt: '2026-12-31T16:00:00Z',
  },
  {
    id: 'de90c87a-bc83-437a-86c4-61c1c28ccb03',
    name: 'legacy-monitor',
    prefix: 'msk_live_1RT6',
    createdBy: 'preview-admin',
    createdAt: '2026-02-08T07:20:00Z',
    revokedAt: '2026-08-29T04:10:00Z',
  },
]
const previewApiKeyGrants: Record<string, MockPermissionGrant[]> = {
  'de90c87a-bc83-437a-86c4-61c1c28ccb01': [
    { permissionCode: 'dashboard.read', resourceType: '', resourceKey: '' },
    { permissionCode: 'queue.read', resourceType: '', resourceKey: '' },
  ],
  'de90c87a-bc83-437a-86c4-61c1c28ccb02': [
    { permissionCode: 'settings.read', resourceType: '', resourceKey: '' },
    { permissionCode: 'domain.read', resourceType: 'domain', resourceKey: '*' },
  ],
  'de90c87a-bc83-437a-86c4-61c1c28ccb03': [
    { permissionCode: 'dashboard.read', resourceType: '', resourceKey: '' },
  ],
}
const previewTasks: MockScheduledTask[] = [
  {
    id: 'f0e1d2c3-b4a5-4678-9012-000000000001', type: 'cache.cleanup', name: '缓存清理',
    isSystem: true, isEnabled: true, intervalMinutes: 60, timeoutSeconds: 600,
    batchSize: 500, maximumAttempts: 3, status: 'Idle', nextRunAt: '2026-09-18T05:00:00Z', lastRunAt: '2026-09-18T04:00:00Z',
  },
  {
    id: 'f0e1d2c3-b4a5-4678-9012-000000000002', type: 'cleanup.recycle', name: '回收队列清理',
    isSystem: true, isEnabled: true, intervalMinutes: 1440, timeoutSeconds: 900,
    batchSize: 100, maximumAttempts: 3, status: 'Idle', nextRunAt: '2026-09-19T02:00:00Z', lastRunAt: '2026-09-18T02:00:00Z',
  },
  {
    id: 'f0e1d2c3-b4a5-4678-9012-000000000004', type: 'quota.reconcile', name: '配额重新计算',
    isSystem: true, isEnabled: false, intervalMinutes: 43200, timeoutSeconds: 3600,
    batchSize: 100, maximumAttempts: 3, status: 'Paused', nextRunAt: '2026-10-01T02:00:00Z', lastRunAt: '2026-09-01T02:00:00Z',
  },
  {
    id: 'f0e1d2c3-b4a5-4678-9012-000000000005', type: 'storage.integrity-check', name: '存储完整性检查',
    isSystem: true, isEnabled: true, intervalMinutes: 1440, timeoutSeconds: 7200,
    batchSize: 1000, maximumAttempts: 3, status: 'Running', nextRunAt: '2026-09-19T03:00:00Z', lastRunAt: '2026-09-18T04:18:00Z',
  },
]

let previewTaskExecutions: MockTaskExecution[] = [
  { id: '0aa00000-0000-4000-8000-000000000001', scheduledTaskId: 'f0e1d2c3-b4a5-4678-9012-000000000005', status: 'Running', startedAt: '2026-09-18T04:18:00Z', processedCount: 386, releasedBytes: 0, attempt: 1 },
  { id: '0aa00000-0000-4000-8000-000000000003', scheduledTaskId: 'f0e1d2c3-b4a5-4678-9012-000000000001', status: 'Succeeded', startedAt: '2026-09-18T04:00:00Z', completedAt: '2026-09-18T04:00:03Z', processedCount: 218, releasedBytes: 73400320, attempt: 1 },
  { id: '0aa00000-0000-4000-8000-000000000004', scheduledTaskId: 'f0e1d2c3-b4a5-4678-9012-000000000002', status: 'Succeeded', startedAt: '2026-09-18T02:00:00Z', completedAt: '2026-09-18T02:00:11Z', processedCount: 14, releasedBytes: 187695104, attempt: 1 },
  { id: '0aa00000-0000-4000-8000-000000000005', scheduledTaskId: 'f0e1d2c3-b4a5-4678-9012-000000000004', status: 'Succeeded', startedAt: '2026-09-01T02:00:00Z', completedAt: '2026-09-01T02:01:22Z', processedCount: 248, releasedBytes: 0, attempt: 1 },
]

let previewAuditEntries: MockAuditEntry[] = [
  {
    id: '1a100000-0000-4000-8000-000000000001', number: '1a100000', content: '执行后台任务', category: 'BackgroundTask', status: 'Failed',
    actor: 'system', action: 'task.run', resource: 'task:quota.reconcile', detail: '配额台账与邮箱用量不一致，已中止并等待下次重试', remoteAddress: '127.0.0.1',
    requestId: 'req-task-9041', relatedId: '0aa00000-0000-4000-8000-000000000003', durationMilliseconds: 8042,
    createdAt: '2026-09-18T04:20:08Z', changes: [],
    rawJson: JSON.stringify({ action: 'task.run', task: 'quota.reconcile', status: 'Failed', token: '***', operationIp: '127.0.0.1' }, null, 2),
  },
  {
    id: '1a100000-0000-4000-8000-000000000002', number: '1a100001', content: '邮件格式校验 插件隔离邮件', category: 'Plugin', status: 'Warning',
    actor: '邮件格式校验', action: 'plugin.runtime.quarantine', resource: 'message:7d7f', detail: 'MIME 附件类型需要人工审核',
    requestId: 'req-smtp-2208', durationMilliseconds: 42, pluginId: 'mime', pluginName: '邮件格式校验', createdAt: '2026-09-18T04:12:31Z', changes: [],
    rawJson: JSON.stringify({ pluginId: 'mime', stage: 'MessageReceived', decision: 'Quarantine', credential: '***' }, null, 2),
  },
  {
    id: '1a100000-0000-4000-8000-000000000003', number: '1a100002', content: '编辑域名', category: 'Update', status: 'Succeeded',
    actor: 'preview-admin', action: 'domain.update', resource: 'domain:northwind.cn', detail: '更新域配额与邮件主机名', remoteAddress: '192.0.2.18',
    requestId: 'req-api-6182', createdAt: '2026-09-18T03:46:15Z',
    changes: [{ field: 'quotaBytes', oldValue: '536870912000', newValue: '1099511627776' }, { field: 'mailHostName', oldValue: 'mx.northwind.cn', newValue: 'mail.northwind.cn' }],
    rawJson: JSON.stringify({ action: 'domain.update', domain: 'northwind.cn', password: '***', operationIp: '192.0.2.18' }, null, 2),
  },
  {
    id: '1a100000-0000-4000-8000-000000000004', number: '1a100003', content: '刷新 API Key', category: 'Security', status: 'Succeeded',
    actor: 'preview-admin', action: 'api-key.refresh', resource: 'api-key:metrics-exporter', detail: '已轮换凭据并撤销旧密钥', remoteAddress: '192.0.2.18',
    requestId: 'req-api-6033', createdAt: '2026-09-18T03:30:02Z', changes: [],
    rawJson: JSON.stringify({ action: 'api-key.refresh', apiKey: '***', operationIp: '192.0.2.18' }, null, 2),
  },
  {
    id: '1a100000-0000-4000-8000-000000000005', number: '1a100004', content: 'DNS 诊断失败', category: 'Other', status: 'Warning',
    actor: 'system', action: 'domain.dns.diagnose', resource: 'domain:northwind.cn', detail: 'SPF 记录未生效，等待 DNS 传播',
    requestId: 'req-dns-3301', durationMilliseconds: 30012, createdAt: '2026-09-18T03:25:47Z', changes: [],
    rawJson: JSON.stringify({ domain: 'northwind.cn', record: 'SPF', status: 'missing' }, null, 2),
  },
  {
    id: '1a100000-0000-4000-8000-000000000006', number: '1a100005', content: '创建管理员', category: 'Create', status: 'Succeeded',
    actor: 'preview-admin', action: 'admin.create', resource: 'admin:audit-reviewer', detail: '创建只读审计管理员', remoteAddress: '192.0.2.18',
    requestId: 'req-api-5118', createdAt: '2026-09-17T12:40:00Z', changes: [],
    rawJson: JSON.stringify({ userName: 'audit-reviewer', permissions: ['audit.read'] }, null, 2),
  },
  {
    id: '1a100000-0000-4000-8000-000000000007', number: '1a100006', content: '删除防火墙规则', category: 'Delete', status: 'Succeeded',
    actor: 'preview-admin', action: 'firewall.delete', resource: 'firewall:198.51.100.0/24', detail: '移除已过期的临时阻断规则', remoteAddress: '192.0.2.18',
    requestId: 'req-api-4880', createdAt: '2026-09-17T10:15:00Z', changes: [],
    rawJson: JSON.stringify({ cidr: '198.51.100.0/24', operationIp: '192.0.2.18' }, null, 2),
  },
  {
    id: '1a100000-0000-4000-8000-000000000008', number: '1a100007', content: '插件加载失败', category: 'Plugin', status: 'Failed',
    actor: 'plugin:policy-demo', action: 'plugin.load.failed', resource: 'plugin:policy-demo', detail: '程序集签名验证失败',
    requestId: 'req-plugin-4012', pluginId: 'policy-demo', pluginName: '策略演示插件', createdAt: '2026-09-17T09:05:00Z', changes: [],
    rawJson: JSON.stringify({ pluginId: 'policy-demo', error: 'signature validation failed' }, null, 2),
  },
]

function addPreviewAudit(input: Omit<MockAuditEntry, 'id' | 'number' | 'rawJson'> & { rawJson?: string }) {
  const id = globalThis.crypto.randomUUID()
  previewAuditEntries = [{
    ...input,
    id,
    number: id.replaceAll('-', '').slice(0, 8),
    rawJson: input.rawJson ?? JSON.stringify({ action: input.action, resource: input.resource, actor: input.actor }, null, 2),
  }, ...previewAuditEntries]
}

const previewSettings: Record<string, Record<string, unknown>> = {
  '/settings/delivery': { batchSize: 20, leaseSeconds: 120, maximumAttempts: 5, pollIntervalSeconds: 5 },
  '/settings/smtp': { commandTimeoutSeconds: 300 },
  '/settings/mailboxes': { defaultFolders: ['INBOX', 'Sent', 'Drafts', 'Trash', 'Junk', 'Archives', 'Customers'] },
  '/settings/certificates': {
    defaultMailHostName: 'mail.northwind.cn',
    allowSingleCertificateFallback: false,
    availableMailHostNames: ['mail.northwind.cn', 'mx.acme-mail.com'],
    effectiveDefaultMailHostName: 'mail.northwind.cn',
  },
  '/settings/message-templates': { senderDisplayName: '邮件投递系统', permanentFailureSubject: '邮件投递失败', permanentFailureBody: '<p>收件人：{recipient}</p><p>失败原因：{reason}</p>', retryExhaustedSubject: '邮件重试次数已用尽', retryExhaustedBody: '<p>向 {recipient} 投递已尝试 {attempts} 次。</p><p>{reason}</p>' },
}
const previewPlugins: MockPlugin[] = [
  {
    id: 'mailserver-mime-validation', displayName: 'MIME 安全验证', description: '验证 MIME 结构、8BITMIME 声明、部件数量和嵌套深度。',
    version: '1.0.0', system: true, enabled: true, isHostActive: true, order: 10, effectiveOrder: 10,
  },
  {
    id: 'mailserver-greylisting', displayName: '灰名单', description: '首次遇到新的 SMTP 发件组合时临时延迟，正常重试后自动放行。',
    version: '1.0.0', system: true, enabled: true, isHostActive: true, order: 20, effectiveOrder: 20,
  },
  {
    id: 'sample-large-message-policy', displayName: '示例大邮件与 Bearer 认证策略', description: '开发示例插件；生产环境不应使用示例 Token 验证。',
    version: '1.0.0', system: false, enabled: false, isHostActive: true, order: 100, effectiveOrder: 30,
  },
  {
    id: 'mailserver-testplugin', displayName: '测试策略插件', description: '演示加载失败时的诊断与停用流程。',
    version: '1.0.0', system: false, enabled: true, isHostActive: false, loadError: '程序集签名验证失败', order: 110, effectiveOrder: 40,
  },
]

const previewPluginConfigurations: Record<string, MockPluginConfiguration> = {
  'mailserver-mime-validation': {
    definitions: [
      { key: 'requireEightBitMimeDeclaration', type: 'Boolean', label: '要求声明 8BITMIME', description: '正文包含 8 位字节时必须声明 BODY=8BITMIME。', options: [] },
      { key: 'validateMimeStructure', type: 'Boolean', label: '验证 MIME 结构', description: '拒绝无法解析或结构异常的 MIME 消息。', options: [] },
      { key: 'maximumMimeParts', type: 'Integer', label: '最大 MIME 部件数', description: '包含 multipart 容器在内的最大部件数量。', minimum: 1, maximum: 10000, options: [] },
      { key: 'maximumNestingDepth', type: 'Integer', label: '最大嵌套深度', minimum: 1, maximum: 100, options: [] },
      { key: 'emptyBodyPolicy', type: 'Select', label: '空邮件策略', options: [{ value: 'accept', label: '接受' }, { value: 'reject', label: '拒绝' }] },
      { key: 'headerOnlyPolicy', type: 'Select', label: '仅邮件头策略', options: [{ value: 'accept', label: '接受' }, { value: 'reject', label: '拒绝' }] },
    ],
    values: [
      { key: 'requireEightBitMimeDeclaration', value: true, hasSecret: false },
      { key: 'validateMimeStructure', value: true, hasSecret: false },
      { key: 'maximumMimeParts', value: 1000, hasSecret: false },
      { key: 'maximumNestingDepth', value: 30, hasSecret: false },
      { key: 'emptyBodyPolicy', value: 'accept', hasSecret: false },
      { key: 'headerOnlyPolicy', value: 'accept', hasSecret: false },
    ],
  },
  'mailserver-greylisting': {
    definitions: [
      { key: 'minimumRetryMinutes', type: 'Integer', label: '最短重试等待（分钟）', minimum: 1, maximum: 1440, options: [] },
      { key: 'recordExpiryDays', type: 'Integer', label: '记录有效期（天）', minimum: 1, maximum: 365, options: [] },
      { key: 'includeRemoteIp', type: 'Boolean', label: '匹配远端 IP', options: [] },
      { key: 'includeSender', type: 'Boolean', label: '匹配 Envelope 发件人', options: [] },
      { key: 'includeRecipients', type: 'Boolean', label: '匹配收件人集合', options: [] },
      { key: 'bypassLocalDomains', type: 'Boolean', label: '默认放行本地域名', options: [] },
      { key: 'bypassDomains', type: 'Text', label: '放行域名列表', description: '支持逗号、空格或换行分隔。', placeholder: 'example.com, partner.test', options: [] },
    ],
    values: [
      { key: 'minimumRetryMinutes', value: 5, hasSecret: false },
      { key: 'recordExpiryDays', value: 30, hasSecret: false },
      { key: 'includeRemoteIp', value: true, hasSecret: false },
      { key: 'includeSender', value: true, hasSecret: false },
      { key: 'includeRecipients', value: true, hasSecret: false },
      { key: 'bypassLocalDomains', value: true, hasSecret: false },
      { key: 'bypassDomains', value: 'partner.example', hasSecret: false },
    ],
  },
  'sample-large-message-policy': {
    definitions: [
      { key: 'exampleIdentity', type: 'Text', label: '示例映射邮箱地址', description: '仅用于开发验证。', required: true, placeholder: 'developer@example.test', options: [] },
      { key: 'exampleBearerToken', type: 'Password', label: '示例 Bearer Token', description: '仅用于开发和测试，不应用于生产环境。', options: [] },
    ],
    values: [
      { key: 'exampleIdentity', value: 'developer@northwind.cn', hasSecret: false },
      { key: 'exampleBearerToken', value: null, hasSecret: true },
    ],
  },
  'mailserver-testplugin': { definitions: [], values: [] },
}
let previewRecycle: MockRecycleBinItem[] = [
  {
    queueId: 'cf704ce9-cb1c-49ac-b4ec-01ac06aaf801', resourceType: 'MailboxMessage', resourceId: '60a9ad2c-9c08-4a9a-9a18-a8ebca920101',
    displayName: 'billing@northwind.cn / Sent / UID 48291', estimatedBytes: 18_874_368,
    deletedAt: '2026-09-17T08:24:00Z', purgeAfter: '2026-09-19T08:24:00Z', status: 'Pending',
  },
  {
    queueId: 'd23f11a8-8c67-47b1-90be-d22c62d7f802', resourceType: 'Domain', resourceId: '9e8fef40-335f-4d7a-a6c5-7379bb9aa404',
    displayName: 'archive.example', estimatedBytes: 60_129_542_144,
    deletedAt: '2026-09-12T03:18:00Z', purgeAfter: '2026-10-12T03:18:00Z', status: 'Pending',
  },
  {
    queueId: 'a86c480b-3777-42bb-bbbd-8df202e2f803', resourceType: 'Account', resourceId: '60a9ad2c-9c08-4a9a-9a18-a8ebca920103',
    displayName: 'retired@northwind.cn', estimatedBytes: 4_294_967_296,
    deletedAt: '2026-09-14T08:00:00Z', purgeAfter: '2026-10-14T08:00:00Z', status: 'Pending',
  },
  {
    queueId: '531f102d-e2a7-4235-bff7-f522ea70f804', resourceType: 'Mailbox', resourceId: '60a9ad2c-9c08-4a9a-9a18-a8ebca920104',
    displayName: 'support@acme-mail.com / Archive', estimatedBytes: 805_306_368,
    deletedAt: '2026-09-03T01:12:00Z', purgeAfter: '2026-10-03T01:12:00Z', status: 'Failed',
  },
  {
    queueId: '98be027a-4fa6-421f-a546-98a90b1bf805', resourceType: 'ContentFile', resourceId: null,
    displayName: 'sha256:b4e6…7a12.eml', estimatedBytes: 7_340_032,
    deletedAt: '2026-08-16T13:42:00Z', purgeAfter: '2026-09-15T13:42:00Z', status: 'Failed',
  },
]
const iso = (minutes: number) => new Date(now.getTime() + minutes * 60_000).toISOString()

const domains: MockDomain[] = [
  { id: '9e8fef40-335f-4d7a-a6c5-7379bb9aa401', name: 'northwind.cn', mailHostName: 'mail.northwind.cn', isActive: true, dnsStatus: 'Healthy', dnsCheckedAt: iso(-18), hasCertificate: true, certificateNotAfter: '2027-02-18T00:00:00Z', accountCount: 84, quotaBytes: 1099511627776, usedBytes: 421336088576, createdAt: '2026-02-12T08:00:00Z' },
  { id: '9e8fef40-335f-4d7a-a6c5-7379bb9aa402', name: 'acme-mail.com', mailHostName: 'mx.acme-mail.com', isActive: true, dnsStatus: 'Unhealthy', dnsCheckedAt: iso(-43), hasCertificate: true, certificateNotAfter: '2026-09-19T00:00:00Z', accountCount: 126, quotaBytes: 2199023255552, usedBytes: 751619276800, createdAt: '2026-03-20T08:00:00Z' },
  { id: '9e8fef40-335f-4d7a-a6c5-7379bb9aa403', name: 'studio.dev', mailHostName: 'mail.studio.dev', isActive: true, dnsStatus: 'PendingConfiguration', hasCertificate: false, accountCount: 38, quotaBytes: 536870912000, usedBytes: 192199786496, createdAt: '2026-07-01T08:00:00Z' },
  { id: '9e8fef40-335f-4d7a-a6c5-7379bb9aa404', name: 'archive.example', mailHostName: 'mail.archive.example', isActive: false, dnsStatus: 'PendingConfiguration', hasCertificate: false, accountCount: 12, quotaBytes: 214748364800, usedBytes: 60129542144, createdAt: '2025-11-08T08:00:00Z' },
]

const domainAccounts: Record<string, MockDomainAccount[]> = {
  '9e8fef40-335f-4d7a-a6c5-7379bb9aa401': [
    { id: 'f1cfe00a-f41b-43e2-a7a3-9f0600000001', address: 'billing@northwind.cn', isEnabled: true, quotaBytes: 21_474_836_480, usedBytes: 8_912_896_000, createdAt: '2026-02-12T09:00:00Z', aliasCount: 3 },
    { id: 'f1cfe00a-f41b-43e2-a7a3-9f0600000002', address: 'support@northwind.cn', isEnabled: true, quotaBytes: 10_737_418_240, usedBytes: 7_044_661_248, createdAt: '2026-02-13T09:00:00Z', aliasCount: 5 },
    { id: 'f1cfe00a-f41b-43e2-a7a3-9f0600000003', address: 'security@northwind.cn', isEnabled: true, quotaBytes: 10_737_418_240, usedBytes: 2_147_483_648, createdAt: '2026-03-01T09:00:00Z', aliasCount: 1 },
    { id: 'f1cfe00a-f41b-43e2-a7a3-9f0600000004', address: 'legacy@northwind.cn', isEnabled: false, quotaBytes: 5_368_709_120, usedBytes: 4_831_838_208, createdAt: '2026-03-08T09:00:00Z', aliasCount: 0 },
  ],
  '9e8fef40-335f-4d7a-a6c5-7379bb9aa402': [
    { id: 'f1cfe00a-f41b-43e2-a7a3-9f0600000011', address: 'ops@acme-mail.com', isEnabled: true, quotaBytes: 21_474_836_480, usedBytes: 12_884_901_888, createdAt: '2026-03-20T09:00:00Z', aliasCount: 2 },
  ],
}

const domainAliases: Record<string, MockDomainAlias[]> = {
  '9e8fef40-335f-4d7a-a6c5-7379bb9aa401': [
    { id: 'a11a5000-0000-4000-8000-000000000001', source: 'sales@northwind.cn', targetAccountId: 'f1cfe00a-f41b-43e2-a7a3-9f0600000001', targetAddress: 'billing@northwind.cn', isEnabled: true, legacyDestinationAddress: null, mode: 'SendReceive', canReceive: true, canSend: true },
    { id: 'a11a5000-0000-4000-8000-000000000002', source: 'help@northwind.cn', targetAccountId: 'f1cfe00a-f41b-43e2-a7a3-9f0600000002', targetAddress: 'support@northwind.cn', isEnabled: true, legacyDestinationAddress: null, mode: 'ReceiveOnly', canReceive: true, canSend: false },
    { id: 'a11a5000-0000-4000-8000-000000000003', source: 'abuse@northwind.cn', targetAccountId: 'f1cfe00a-f41b-43e2-a7a3-9f0600000003', targetAddress: 'security@northwind.cn', isEnabled: true, legacyDestinationAddress: null, mode: 'ReceiveOnly', canReceive: true, canSend: false },
  ],
}

// 分页验收需要足量数据：为 northwind.cn 补齐 50 个账号与 50 个别名（合计 54 / 53）。
const previewNorthwindAccounts = domainAccounts['9e8fef40-335f-4d7a-a6c5-7379bb9aa401']!
for (let index = 1; index <= 50; index++) {
  const localPart = `team${String(index).padStart(2, '0')}`
  previewNorthwindAccounts.push({
    id: `f1cfe00a-f41b-43e2-a7a3-9f06${String(1000 + index).padStart(6, '0')}`,
    address: `${localPart}@northwind.cn`,
    isEnabled: index % 7 !== 0,
    quotaBytes: 10_737_418_240,
    usedBytes: (index * 137_000_000) % 8_000_000_000,
    createdAt: new Date(Date.UTC(2026, 3, 1 + (index % 25), 9)).toISOString(),
    aliasCount: 0,
  })
}

const previewNorthwindAliases = domainAliases['9e8fef40-335f-4d7a-a6c5-7379bb9aa401']!
for (let index = 1; index <= 50; index++) {
  const target = previewNorthwindAccounts[index % previewNorthwindAccounts.length]!
  previewNorthwindAliases.push({
    id: `a11a5000-0000-4000-8000-0000000001${String(index).padStart(2, '0')}`,
    source: `alias${String(index).padStart(2, '0')}@northwind.cn`,
    targetAccountId: target.id,
    targetAddress: target.address,
    isEnabled: index % 11 !== 0,
    legacyDestinationAddress: null,
    mode: index % 3 === 0 ? 'ReceiveOnly' : 'SendReceive',
    canReceive: true,
    canSend: index % 3 !== 0,
  })
  target.aliasCount++
}

const domainRules: Record<string, MockDomainRule[]> = {
  '9e8fef40-335f-4d7a-a6c5-7379bb9aa401': [
    { id: '771e0000-0000-4000-8000-000000000001', domainId: '9e8fef40-335f-4d7a-a6c5-7379bb9aa401', name: '隔离高风险附件', direction: 'Incoming', matchMode: 'Any', targetMailboxName: 'Junk', isEnabled: true, priority: 0, conditions: [{ id: 'c1', field: 'AttachmentName', operator: 'ContainsAny', values: ['.exe', '.js'], position: 0 }], actions: [{ id: 'a1', kind: 'MoveToFolder', values: ['Junk'], position: 0 }] },
    { id: '771e0000-0000-4000-8000-000000000002', domainId: '9e8fef40-335f-4d7a-a6c5-7379bb9aa401', name: '财务通知归档', direction: 'Incoming', matchMode: 'All', targetMailboxName: 'INBOX', isEnabled: true, priority: 1, conditions: [{ id: 'c2', field: 'Recipient', operator: 'EqualsAny', values: ['billing@northwind.cn'], position: 0 }], actions: [{ id: 'a2', kind: 'ForwardTo', values: ['finance@northwind.cn'], position: 0 }] },
    { id: '771e0000-0000-4000-8000-000000000003', domainId: '9e8fef40-335f-4d7a-a6c5-7379bb9aa401', name: '营销邮件归档', direction: 'Incoming', matchMode: 'Any', targetMailboxName: 'Archives', isEnabled: false, priority: 2, conditions: [{ id: 'c3', field: 'Subject', operator: 'ContainsAny', values: ['*newsletter*', '*促销*'], position: 0 }], actions: [{ id: 'a3', kind: 'MoveToFolder', values: ['Archives'], position: 0 }] },
    { id: '771e0000-0000-4000-8000-000000000101', domainId: '9e8fef40-335f-4d7a-a6c5-7379bb9aa401', name: '禁止外发含附件的敏感主题', direction: 'Outgoing', matchMode: 'Any', targetMailboxName: 'Sent', isEnabled: true, priority: 0, conditions: [{ id: 'c4', field: 'Subject', operator: 'ContainsAny', values: ['*机密*', '*confidential*'], position: 0 }], actions: [{ id: 'a4', kind: 'Reject', values: ['该主题禁止外发'], position: 0 }] },
    { id: '771e0000-0000-4000-8000-000000000102', domainId: '9e8fef40-335f-4d7a-a6c5-7379bb9aa401', name: '外发副本抄送合规邮箱', direction: 'Outgoing', matchMode: 'All', targetMailboxName: 'Sent', isEnabled: true, priority: 1, conditions: [{ id: 'c5', field: 'Recipient', operator: 'ContainsAny', values: ['*@partner.test'], position: 0 }], actions: [{ id: 'a5', kind: 'ForwardTo', values: ['compliance@northwind.cn'], position: 0 }] },
  ],
}


const domainQuarantine: Record<string, MockQuarantineItem[]> = {
  '9e8fef40-335f-4d7a-a6c5-7379bb9aa401': [
    {
      id: '3b1a6d24-51c8-4f0e-9d2a-6f7c18b9a101', domainId: '9e8fef40-335f-4d7a-a6c5-7379bb9aa401',
      messageId: 'qmsg-20260918-0001', sender: 'promo@shady.example', recipients: ['billing@northwind.cn'],
      remoteAddress: '203.0.113.77', pluginId: 'mailserver-antivirus', pluginName: '病毒防护',
      reason: '附件命中高风险特征：Win32.Trojan.Generic', status: 'PendingReview',
      quarantinedAt: iso(-128), reviewedBy: null, reviewedAt: null, reviewNote: null,
      size: 7_340_032, internetMessageId: '<invoice-8812@shady.example>',
    },
    {
      id: '7c4e9a11-2d63-4b57-8a10-9e5f3c02b102', domainId: '9e8fef40-335f-4d7a-a6c5-7379bb9aa401',
      messageId: 'qmsg-20260918-0002', sender: 'noreply@spoofed-partner.test', recipients: ['support@northwind.cn'],
      remoteAddress: '198.51.100.203', pluginId: 'mailserver-spf', pluginName: 'SPF/DMARC 校验',
      reason: 'DMARC 对齐失败且发件域策略为 quarantine', status: 'PendingReview',
      quarantinedAt: iso(-64), reviewedBy: null, reviewedAt: null, reviewNote: null,
      size: 184_320, internetMessageId: '<billing-update@spoofed-partner.test>',
    },
    {
      id: 'a5f8b730-9c14-4e2b-bd63-2c7ea410c103', domainId: '9e8fef40-335f-4d7a-a6c5-7379bb9aa401',
      messageId: 'qmsg-20260917-0007', sender: 'macro-doc@unknown-sender.example', recipients: ['security@northwind.cn'],
      remoteAddress: '192.0.2.145', pluginId: 'mailserver-mime', pluginName: 'MIME 结构校验',
      reason: '宏文档附件，等待人工判定', status: 'PendingReview',
      quarantinedAt: iso(-540), reviewedBy: null, reviewedAt: null, reviewNote: null,
      size: 2_621_440, internetMessageId: '<quarterly-plan@unknown-sender.example>',
    },
    {
      id: 'c9d2e451-8f3a-4a76-9c08-1b6d5e27d104', domainId: '9e8fef40-335f-4d7a-a6c5-7379bb9aa401',
      messageId: 'qmsg-20260916-0011', sender: 'newsletter@marketing.example', recipients: ['billing@northwind.cn'],
      remoteAddress: '203.0.113.19', pluginId: null, pluginName: null,
      reason: '命中内容过滤规则：批量营销内容', status: 'Released',
      quarantinedAt: iso(-1980), reviewedBy: 'preview-admin', reviewedAt: iso(-1900),
      reviewNote: '已确认业务伙伴正常订阅，放行。', size: 96_256, internetMessageId: '<weekly-42@marketing.example>',
    },
    {
      id: 'ef0b8c62-4d95-4b18-a7f3-5e2c9016e105', domainId: '9e8fef40-335f-4d7a-a6c5-7379bb9aa401',
      messageId: 'qmsg-20260915-0003', sender: 'phish@credential-harvest.example', recipients: ['support@northwind.cn'],
      remoteAddress: '192.0.2.88', pluginId: 'mailserver-phishing', pluginName: '钓鱼检测',
      reason: '仿冒登录页面链接', status: 'Deleted',
      quarantinedAt: iso(-3600), reviewedBy: 'preview-admin', reviewedAt: iso(-3500),
      reviewNote: '确认为钓鱼邮件，直接删除。', size: 51_200, internetMessageId: '<verify-account@credential-harvest.example>',
    },
  ],
}

const domainAuditEntries: Record<string, MockDomainAuditEntry[]> = {
  '9e8fef40-335f-4d7a-a6c5-7379bb9aa401': [
    { id: 'da000000-0000-4000-8000-000000000001', actor: 'preview-admin', action: 'domain.certificate.upload', resource: 'domain:9e8fef40-335f-4d7a-a6c5-7379bb9aa401', detail: '上传并启用 TLS 证书 mail.northwind.cn', createdAt: iso(-60) },
    { id: 'da000000-0000-4000-8000-000000000002', actor: 'preview-admin', action: 'rule.create', resource: 'rule:771e0000-0000-4000-8000-000000000002', detail: '创建收发信规则「财务通知归档」', createdAt: iso(-420) },
    { id: 'da000000-0000-4000-8000-000000000003', actor: 'preview-admin', action: 'alias.create', resource: 'alias:a11a5000-0000-4000-8000-000000000003', detail: 'abuse@northwind.cn -> security@northwind.cn', createdAt: iso(-1440) },
    { id: 'da000000-0000-4000-8000-000000000004', actor: 'preview-admin', action: 'account.create', resource: 'account:f1cfe00a-f41b-43e2-a7a3-9f0600000004', detail: 'legacy@northwind.cn', createdAt: iso(-2880) },
    { id: 'da000000-0000-4000-8000-000000000005', actor: 'preview-admin', action: 'domain.dns.update', resource: 'domain:9e8fef40-335f-4d7a-a6c5-7379bb9aa401', detail: 'DMARC 策略调整为 quarantine', createdAt: iso(-4320) },
  ],
}

function createDnsCheck(domain: MockDomain): MockDnsCheck {
  if (domain.dnsStatus === 'PendingConfiguration') return { domainId: domain.id, status: domain.dnsStatus, validation: null, lastError: null, checkedAt: null }
  const healthy = domain.dnsStatus === 'Healthy'
  const selector = domain.id.endsWith('401') ? 'mail2026' : 'mail'
  return {
    domainId: domain.id,
    status: domain.dnsStatus,
    validation: {
      domain: domain.name,
      items: [
        { kind: 'MX', name: domain.name, expected: `10 ${domain.mailHostName}`, actual: healthy ? [`10 ${domain.mailHostName}`] : ['10 mx.old-provider.example'], status: healthy ? 'match' : 'conflict' },
        { kind: 'A/AAAA', name: domain.mailHostName, expected: '可公开解析的 IPv4 或 IPv6 地址', actual: ['20.24.67.236'], status: 'match' },
        { kind: 'SPF', name: domain.name, expected: 'v=spf1 mx -all', actual: healthy ? ['v=spf1 mx -all'] : ['v=spf1 ~all'], status: healthy ? 'match' : 'conflict' },
        { kind: 'DKIM', name: `${selector}._domainkey.${domain.name}`, expected: 'v=DKIM1; k=rsa; p=…', actual: ['v=DKIM1; k=rsa; p=MIIB…'], status: 'match' },
        { kind: 'DMARC', name: `_dmarc.${domain.name}`, expected: 'v=DMARC1; p=quarantine', actual: ['v=DMARC1; p=quarantine'], status: 'match' },
      ],
      warnings: healthy ? [`PTR 反向解析尚未指向 ${domain.mailHostName}`] : ['当前 MX 记录仍指向旧邮件服务商。'],
    },
    lastError: null,
    checkedAt: domain.dnsCheckedAt ?? new Date().toISOString(),
  }
}

const previewDnsChecks: Record<string, MockDnsCheck> = Object.fromEntries(domains.map(domain => [domain.id, createDnsCheck(domain)]))

function createPreviewDnsSettings(domain: MockDomain): Record<string, unknown> {
  return {
    domainId: domain.id, spfIncludeMx: true, spfIncludeA: false, spfIpv4: '', spfIpv6: '',
    spfIncludes: '', spfCustomMechanisms: '', spfAllPolicy: '-all',
    dmarcPolicy: 'quarantine', dmarcSubdomainPolicy: 'quarantine', dmarcPercentage: 100,
    dmarcAggregateReportUris: `mailto:dmarc@${domain.name}`, dmarcForensicReportUris: '',
    dmarcDkimAlignment: 'r', dmarcSpfAlignment: 'r', dmarcFailureOptions: '0',
    mtaStsMode: 'enforce', mtaStsPolicyId: '20260918', mtaStsMaxAgeSeconds: 604800,
    mtaStsMxPatterns: domain.mailHostName, tlsReportUris: `mailto:tls-rpt@${domain.name}`,
    daneEnabled: false, daneTlsaRecords: '', updatedAt: '2026-09-18T03:00:00Z',
  }
}

const previewDnsSettings: Record<string, Record<string, unknown>> = Object.fromEntries(
  domains.map(domain => [domain.id, createPreviewDnsSettings(domain)]),
)

const previewDkim: Record<string, { selector: string; publicKey: string }> = Object.fromEntries(
  domains.map(domain => [domain.id, {
    selector: domain.id.endsWith('401') ? 'mail2026' : 'mail',
    publicKey: 'MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAuG7f2kQm1sV4pLd9xY3bC6nR8aF1eT5oIw==',
  }]),
)

function splitPreviewList(value: unknown): string[] {
  return String(value ?? '').split(/[\s,;]+/).map(item => item.trim()).filter(Boolean)
}

function generatePreviewDnsRecords(domain: MockDomain, settings: Record<string, unknown>) {
  const records: Array<{ name: string; type: string; value: string; zoneFileValue: string }> = []
  const txt = (name: string, value: string) => records.push({ name, type: 'TXT', value, zoneFileValue: `${name}. 3600 IN TXT "${value}"` })
  const spf = ['v=spf1']
  if (settings.spfIncludeMx) spf.push('mx')
  if (settings.spfIncludeA) spf.push('a')
  splitPreviewList(settings.spfIpv4).forEach(value => spf.push(`ip4:${value}`))
  splitPreviewList(settings.spfIpv6).forEach(value => spf.push(`ip6:${value}`))
  splitPreviewList(settings.spfIncludes).forEach(value => spf.push(`include:${value}`))
  splitPreviewList(settings.spfCustomMechanisms).forEach(value => spf.push(value))
  spf.push(String(settings.spfAllPolicy || '~all'))
  txt(domain.name, spf.join(' '))
  const dmarc = ['v=DMARC1', `p=${settings.dmarcPolicy}`]
  if (settings.dmarcSubdomainPolicy) dmarc.push(`sp=${settings.dmarcSubdomainPolicy}`)
  dmarc.push(`pct=${settings.dmarcPercentage}`)
  if (String(settings.dmarcAggregateReportUris ?? '').trim()) dmarc.push(`rua=${splitPreviewList(settings.dmarcAggregateReportUris).join(',')}`)
  if (String(settings.dmarcForensicReportUris ?? '').trim()) dmarc.push(`ruf=${splitPreviewList(settings.dmarcForensicReportUris).join(',')}`)
  dmarc.push(`adkim=${settings.dmarcDkimAlignment}`, `aspf=${settings.dmarcSpfAlignment}`, `fo=${settings.dmarcFailureOptions || '0'}`)
  txt(`_dmarc.${domain.name}`, dmarc.join('; '))
  const dkim = previewDkim[domain.id]
  if (dkim?.selector && dkim.publicKey) txt(`${dkim.selector}._domainkey.${domain.name}`, `v=DKIM1; k=rsa; p=${dkim.publicKey}`)
  if (settings.mtaStsMode !== 'none') txt(`_mta-sts.${domain.name}`, `v=STSv1; id=${settings.mtaStsPolicyId};`)
  if (String(settings.tlsReportUris ?? '').trim()) txt(`_smtp._tls.${domain.name}`, `v=TLSRPTv1; rua=${splitPreviewList(settings.tlsReportUris).join(',')}`)
  if (settings.daneEnabled) {
    splitPreviewList(settings.daneTlsaRecords).forEach(value => records.push({
      name: `_25._tcp.${domain.mailHostName}`, type: 'TLSA', value,
      zoneFileValue: `_25._tcp.${domain.mailHostName}. IN TLSA ${value}`,
    }))
  }
  return records
}

function validatePreviewDnsSettings(input: Record<string, unknown>): string {
  if (!['-all', '~all', '?all', '+all'].includes(String(input.spfAllPolicy))) return 'SPF all 策略无效。'
  if (!['none', 'quarantine', 'reject'].includes(String(input.dmarcPolicy))) return 'DMARC 策略无效。'
  if (input.dmarcSubdomainPolicy && !['none', 'quarantine', 'reject'].includes(String(input.dmarcSubdomainPolicy))) return 'DMARC 子域策略无效。'
  const percentage = Number(input.dmarcPercentage)
  if (!Number.isInteger(percentage) || percentage < 0 || percentage > 100) return 'DMARC 百分比必须是 0 到 100 的整数。'
  if (!/^[A-Za-z0-9]{1,32}$/.test(String(input.mtaStsPolicyId))) return 'MTA-STS 策略 ID 只能是 1-32 位字母或数字。'
  const maxAge = Number(input.mtaStsMaxAgeSeconds)
  if (!Number.isInteger(maxAge) || maxAge < 0 || maxAge > 31_557_600) return 'MTA-STS max_age 必须在 0 到 31557600 秒之间。'
  if (input.mtaStsMode !== 'none' && splitPreviewList(input.mtaStsMxPatterns).length === 0) return '启用 MTA-STS 后必须填写至少一个 MX 模式。'
  return ''
}

/** Managed certificates for domains seeded with hasCertificate, keyed by domain id. */
const previewCertificates: Record<string, MockDomainCertificate> = Object.fromEntries(
  domains.filter(domain => domain.hasCertificate).map(domain => [domain.id, createPreviewCertificate(domain)]),
)

function createPreviewCertificate(domain: MockDomain): MockDomainCertificate {
  return {
    domainId: domain.id,
    source: 'ManagedUpload',
    certificatePath: `managed://${domain.mailHostName}/fullchain.pem`,
    privateKeyPath: `managed://${domain.mailHostName}/private.key`,
    subject: `CN=${domain.mailHostName}`,
    issuer: "Let's Encrypt R13",
    subjectAlternativeNames: domain.mailHostName,
    thumbprint: domain.id.endsWith('401') ? '9A71F0E2D54C8A13' : 'C4B1D87F2E0A9B65',
    notBefore: domain.id.endsWith('401') ? '2026-08-20T00:00:00Z' : '2025-09-18T00:00:00Z',
    notAfter: domain.certificateNotAfter ?? '2027-02-18T00:00:00Z',
    updatedAt: domain.id.endsWith('401') ? '2026-08-20T00:05:00Z' : '2025-09-18T00:05:00Z',
  }
}

function requirePreviewAbsolutePath(value: unknown, label: string): string {
  const path = String(value ?? '').trim()
  if (!/^(?:[a-zA-Z]:[\\/]|\\\\|\/)/.test(path)) throw new Error(`${label}必须是绝对路径。`)
  return path
}

function hashPreview(value: string): string {
  let hash = 0
  for (let index = 0; index < value.length; index += 1) hash = (hash * 31 + value.charCodeAt(index)) >>> 0
  return hash.toString(16).toUpperCase().padStart(8, '0')
}


async function readPreviewFile(value: FormDataEntryValue | null): Promise<string> {
  if (!value) return ''
  if (typeof value === 'string') return value
  return new globalThis.TextDecoder().decode(await value.arrayBuffer())
}

function multipartForm(init: RequestInit): FormData {
  if (!(init.body instanceof FormData)) throw new Error('证书上传必须使用 multipart/form-data 请求')
  return init.body
}

async function readCertificateUpload(init: RequestInit) {
  const form = multipartForm(init)
  return {
    certificatePem: await readPreviewFile(form.get('certificate')),
    privateKeyPem: await readPreviewFile(form.get('privateKey')),
    password: String(form.get('password') ?? ''),
  }
}

function inspectPreviewCertificate(domain: MockDomain, certificatePem: string, privateKeyPem: string, password: string): DomainCertificateInspection {
  if (!certificatePem.includes('BEGIN CERTIFICATE')) throw new Error('证书不是有效的 PEM 格式，缺少 BEGIN CERTIFICATE 标记。')
  if (certificatePem.length > 64_000) throw new Error('证书链超过 1 MiB 限制。')
  if (!/BEGIN [A-Z ]*PRIVATE KEY/.test(privateKeyPem)) throw new Error('私钥不是有效的 PEM 格式，缺少私钥标记。')
  if (password && !privateKeyPem.includes('ENCRYPTED')) throw new Error('私钥未加密，但填写了私钥密码。')
  const lowered = certificatePem.toLowerCase()
  const hasMatchingPrivateKey = password !== 'wrong-password' && !privateKeyPem.toLowerCase().includes('mismatched-key')
  const supportsServerAuthentication = !lowered.includes('clientauth')
  const certificateNames = [...certificatePem.matchAll(/DNS:([a-z0-9.*-]+)/gi)].map(match => match[1]!.toLowerCase())
  const declaredHost = certificatePem.match(/CN=([a-z0-9.*-]+)/i)?.[1]?.toLowerCase()
  const names = certificateNames.length ? certificateNames : declaredHost ? [declaredHost] : [domain.mailHostName]
  const coversMailHostName = names.some(name => name === domain.mailHostName.toLowerCase())
  return {
    subject: `CN=${declaredHost ?? names[0]}`,
    issuer: certificatePem.includes('Preview Intermediate') ? 'Preview Intermediate CA' : "Let's Encrypt R13",
    subjectAlternativeNames: names,
    thumbprint: hashPreview(certificatePem).padEnd(32, '0').slice(0, 32),
    notBefore: '2026-09-01T00:00:00Z',
    notAfter: '2027-09-01T00:00:00Z',
    hasMatchingPrivateKey,
    supportsServerAuthentication,
    coversMailHostName,
  }
}

function savePreviewCertificate(domain: MockDomain, source: MockDomainCertificate['source'], certificatePath: string, privateKeyPath: string, inspection: DomainCertificateInspection): MockDomainCertificate {
  if (!inspection.hasMatchingPrivateKey) throw new Error('证书与私钥不匹配，无法启用。')
  if (!inspection.supportsServerAuthentication) throw new Error('证书不包含 TLS 服务器身份验证用途。')
  if (!inspection.coversMailHostName) throw new Error(`证书 SAN 未覆盖 ${domain.mailHostName}。`)
  const record: MockDomainCertificate = {
    domainId: domain.id,
    source,
    certificatePath,
    privateKeyPath,
    subject: inspection.subject,
    issuer: inspection.issuer,
    subjectAlternativeNames: inspection.subjectAlternativeNames.join('\n'),
    thumbprint: inspection.thumbprint,
    notBefore: inspection.notBefore,
    notAfter: inspection.notAfter,
    updatedAt: new Date().toISOString(),
  }
  previewCertificates[domain.id] = record
  domain.hasCertificate = true
  domain.certificateNotAfter = record.notAfter
  const settings = previewSettings['/settings/certificates']
  if (settings) {
    const available = new Set((settings.availableMailHostNames as string[] | undefined) ?? [])
    available.add(domain.mailHostName)
    settings.availableMailHostNames = [...available].sort((left, right) => left.localeCompare(right))
    settings.effectiveDefaultMailHostName = settings.effectiveDefaultMailHostName ?? domain.mailHostName
  }
  return record
}

let deliveryJobs: MockDeliveryJob[] = [
  { id: '43ddc824-8c8c-40ab-a85b-06f44643ea11', sender: 'billing@northwind.cn', recipient: 'customer@outlook.com', status: 'Deferred', attempts: 2, nextAttemptAt: iso(2), lastError: '421 4.7.0 Remote server temporarily rate limited this sender', createdAt: iso(-38), updatedAt: iso(-6) },
  { id: 'c52f8b8d-27c5-40c1-8558-809190325a22', sender: 'notice@acme-mail.com', recipient: 'team@gmail.com', status: 'Failed', attempts: 5, nextAttemptAt: iso(-1), lastError: 'TLS policy validation failed: certificate name mismatch', createdAt: iso(-96), updatedAt: iso(-24) },
  { id: 'f319915b-e843-44bf-9f42-d248cbbb7b33', sender: 'support@northwind.cn', recipient: 'hello@studio.dev', status: 'Pending', attempts: 0, nextAttemptAt: iso(4), createdAt: iso(-3), updatedAt: iso(-3) },
  { id: 'ac632ced-71e1-4325-b370-86f1777efe44', sender: 'security@northwind.cn', recipient: 'ops@example.net', status: 'Processing', attempts: 1, nextAttemptAt: iso(0), createdAt: iso(-8), updatedAt: iso(-1) },
  { id: 'b8a6c0e5-1028-45a8-b9db-f23bd4f2d610', sender: 'orders@northwind.cn', recipient: 'warehouse@contoso.com', status: 'Delivered', attempts: 1, nextAttemptAt: iso(-2), createdAt: iso(-12), updatedAt: iso(-11.8) },
  { id: '5c223841-ec9e-420f-9160-c2714e8792fb', sender: 'alerts@northwind.cn', recipient: 'noc@legacy-a.example', status: 'Delivered', attempts: 2, nextAttemptAt: iso(-13), createdAt: iso(-19), updatedAt: iso(-13) },
  { id: '02eec0d4-f2a8-4198-a684-39b548d71c35', sender: 'reports@acme-mail.com', recipient: 'closed@example.org', status: 'Failed', attempts: 1, nextAttemptAt: iso(-16), lastError: '550 5.1.1 Recipient address rejected: user unknown', createdAt: iso(-18), updatedAt: iso(-16) },
  { id: '2ad54802-5574-4c31-a5d7-a8f61a2ff809', sender: 'newsletter@northwind.cn', recipient: 'blocked.invalid', status: 'Failed', attempts: 1, nextAttemptAt: iso(-35), lastError: '556 5.1.10 Recipient domain publishes Null MX', createdAt: iso(-36), updatedAt: iso(-35) },
]

const deliveryAttempts: Record<string, MockDeliveryAttempt[]> = {
  '43ddc824-8c8c-40ab-a85b-06f44643ea11': [
    { id: 'a-43-1', attemptNumber: 1, succeeded: false, permanentFailure: false, stage: 'SMTP', mxHost: 'outlook-com.olc.protection.outlook.com', remoteAddress: '52.101.40.2', smtpStatusCode: '421', enhancedStatusCode: '4.7.0', tlsProtocol: 'TLS 1.3', tlsCipherSuite: 'TLS_AES_256_GCM_SHA384', durationMilliseconds: 1842, failureCategory: 'RateLimited', nextRetryAt: iso(-12), response: '421 4.7.0 Temporarily rate limited', sessionSummary: 'S: 220 outlook-com.olc.protection.outlook.com ESMTP\nC: EHLO mail.northwind.cn\nS: 250-STARTTLS\nC: STARTTLS\nS: 220 2.0.0 Ready to start TLS\nC: MAIL FROM:<billing@northwind.cn>\nS: 421 4.7.0 Temporarily rate limited', createdAt: iso(-28) },
    { id: 'a-43-2', attemptNumber: 2, succeeded: false, permanentFailure: false, stage: 'SMTP', mxHost: 'outlook-com.olc.protection.outlook.com', remoteAddress: '52.101.40.2', smtpStatusCode: '421', enhancedStatusCode: '4.7.0', tlsProtocol: 'TLS 1.3', tlsCipherSuite: 'TLS_AES_256_GCM_SHA384', durationMilliseconds: 1640, failureCategory: 'RateLimited', nextRetryAt: iso(2), response: '421 4.7.0 Remote server temporarily rate limited this sender', sessionSummary: 'S: 220 outlook-com.olc.protection.outlook.com ESMTP\nC: EHLO mail.northwind.cn\nS: 250-STARTTLS\nC: STARTTLS\nS: 220 2.0.0 Ready to start TLS\nC: MAIL FROM:<billing@northwind.cn>\nS: 421 4.7.0 Remote server temporarily rate limited this sender', createdAt: iso(-6) },
  ],
  'c52f8b8d-27c5-40c1-8558-809190325a22': Array.from({ length: 5 }, (_, index) => ({ id: `a-c52-${index + 1}`, attemptNumber: index + 1, succeeded: false, permanentFailure: index === 4, stage: 'TLS', mxHost: 'gmail-smtp-in.l.google.com', remoteAddress: '142.250.102.26', smtpStatusCode: '454', enhancedStatusCode: '4.7.5', tlsProtocol: 'TLS 1.3', tlsCipherSuite: 'TLS_AES_256_GCM_SHA384', durationMilliseconds: 902 + index * 130, failureCategory: 'TlsPolicy', nextRetryAt: index === 4 ? undefined : iso(-72 + index * 12), response: 'TLS policy validation failed: certificate name mismatch', sessionSummary: 'S: 220 mx.google.com ESMTP\nC: EHLO mail.acme-mail.com\nS: 250-STARTTLS\nC: STARTTLS\nS: 220 2.0.0 Ready to start TLS\n! Certificate validation failed; peer identity did not satisfy the active MTA-STS policy.', createdAt: iso(-90 + index * 16) })),
  'ac632ced-71e1-4325-b370-86f1777efe44': [
    { id: 'a-ac-1', attemptNumber: 1, succeeded: false, permanentFailure: false, stage: 'CONNECT', mxHost: 'mx.example.net', remoteAddress: '2001:db8::25', durationMilliseconds: 5001, failureCategory: 'ConnectionTimeout', nextRetryAt: iso(0), response: 'IPv6 connection timed out; IPv4 fallback is in progress', sessionSummary: 'CONNECT [2001:db8::25]:25\n! Timeout after 5000 ms\nFALLBACK 203.0.113.25:25', createdAt: iso(-1) },
  ],
  'b8a6c0e5-1028-45a8-b9db-f23bd4f2d610': [
    { id: 'a-b8-1', attemptNumber: 1, succeeded: true, permanentFailure: false, stage: 'SMTP', mxHost: 'mx1.contoso.com', remoteAddress: '203.0.113.18', smtpStatusCode: '250', enhancedStatusCode: '2.0.0', tlsProtocol: 'TLS 1.3', tlsCipherSuite: 'TLS_AES_128_GCM_SHA256', durationMilliseconds: 786, response: '250 2.0.0 Message accepted for delivery', sessionSummary: 'S: 220 mx1.contoso.com ESMTP\nC: EHLO mail.northwind.cn\nC: STARTTLS\nC: MAIL FROM:<orders@northwind.cn>\nC: RCPT TO:<warehouse@contoso.com>\nC: DATA\nS: 250 2.0.0 Message accepted for delivery', createdAt: iso(-11.8) },
  ],
  '5c223841-ec9e-420f-9160-c2714e8792fb': [
    { id: 'a-5c-1', attemptNumber: 1, succeeded: false, permanentFailure: false, stage: 'CONNECT', mxHost: 'mx1.legacy-a.example', remoteAddress: '2001:db8:24::25', durationMilliseconds: 5000, failureCategory: 'ConnectionTimeout', nextRetryAt: iso(-15), response: 'IPv6 connection timed out', sessionSummary: 'CONNECT [2001:db8:24::25]:25\n! Timeout after 5000 ms', createdAt: iso(-18) },
    { id: 'a-5c-2', attemptNumber: 2, succeeded: true, permanentFailure: false, stage: 'SMTP', mxHost: 'mx1.legacy-a.example', remoteAddress: '198.51.100.25', smtpStatusCode: '250', enhancedStatusCode: '2.0.0', tlsProtocol: 'TLS 1.2', tlsCipherSuite: 'TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384', durationMilliseconds: 1220, response: '250 2.0.0 Queued', sessionSummary: 'CONNECT 198.51.100.25:25\nS: 220 mx1.legacy-a.example ESMTP\nC: EHLO mail.northwind.cn\nC: STARTTLS\nC: DATA\nS: 250 2.0.0 Queued', createdAt: iso(-13) },
  ],
  '02eec0d4-f2a8-4198-a684-39b548d71c35': [
    { id: 'a-02-1', attemptNumber: 1, succeeded: false, permanentFailure: true, stage: 'SMTP', mxHost: 'mx.example.org', remoteAddress: '203.0.113.44', smtpStatusCode: '550', enhancedStatusCode: '5.1.1', tlsProtocol: 'TLS 1.3', tlsCipherSuite: 'TLS_AES_128_GCM_SHA256', durationMilliseconds: 620, failureCategory: 'RecipientRejected', response: '550 5.1.1 Recipient address rejected: user unknown', sessionSummary: 'S: 220 mx.example.org ESMTP\nC: EHLO mail.acme-mail.com\nC: MAIL FROM:<reports@acme-mail.com>\nC: RCPT TO:<closed@example.org>\nS: 550 5.1.1 Recipient address rejected: user unknown', createdAt: iso(-16) },
  ],
  '2ad54802-5574-4c31-a5d7-a8f61a2ff809': [
    { id: 'a-2a-1', attemptNumber: 1, succeeded: false, permanentFailure: true, stage: 'DNS', durationMilliseconds: 42, failureCategory: 'NullMx', response: '556 5.1.10 Recipient domain publishes Null MX', sessionSummary: 'DNS MX blocked.invalid\nS: 0 .\n! RFC 7505 Null MX: domain does not accept email', createdAt: iso(-35) },
  ],
}

const tlsReportDeliveries: MockTlsReportDelivery[] = [
  { id: '16f1837d-40bd-4d64-a4b8-6782adb6a2ee', policyDomain: 'outlook.com', reportUri: 'mailto:tlsrpt@outlook.com', periodStart: iso(-48 * 60), periodEnd: iso(-24 * 60), status: 'Completed', attempts: 0, nextAttemptAt: iso(-22 * 60), completedAt: iso(-22 * 60), reportJson: JSON.stringify({ 'organization-name': 'MailServer', 'date-range': { 'start-datetime': iso(-48 * 60), 'end-datetime': iso(-24 * 60) }, 'contact-info': 'postmaster@northwind.cn', 'report-id': 'preview-outlook-20260917', policies: [{ policy: { 'policy-type': 'sts', 'policy-domain': 'outlook.com', 'mx-host': ['*.protection.outlook.com'] }, summary: { 'total-successful-session-count': 842, 'total-failure-session-count': 3 }, 'failure-details': [{ 'result-type': 'certificate-host-mismatch', 'failed-session-count': 3 }] }] }, null, 2) },
  { id: '8ca24f14-df4f-432b-959a-34576f964d25', policyDomain: 'gmail.com', reportUri: 'https://tlsrpt.google.com/report', periodStart: iso(-48 * 60), periodEnd: iso(-24 * 60), status: 'Completed', attempts: 0, nextAttemptAt: iso(-21 * 60), completedAt: iso(-21 * 60), reportJson: JSON.stringify({ 'organization-name': 'MailServer', 'report-id': 'preview-gmail-20260917', policies: [{ policy: { 'policy-type': 'sts', 'policy-domain': 'gmail.com' }, summary: { 'total-successful-session-count': 1210, 'total-failure-session-count': 0 }, 'failure-details': [] }] }, null, 2) },
  { id: '27d8018d-acde-4b9a-9acd-7d7b5c8411a6', policyDomain: 'example.net', reportUri: 'mailto:tls-rpt@example.net', periodStart: iso(-24 * 60), periodEnd: iso(0), status: 'Pending', attempts: 0, nextAttemptAt: iso(60), reportJson: JSON.stringify({ 'organization-name': 'MailServer', 'report-id': 'preview-example-20260918', policies: [] }, null, 2) },
  { id: '97554ea3-24db-4729-9733-d07bd07f4cee', policyDomain: 'legacy-a.example', reportUri: 'https://reports.legacy-a.example/tls', periodStart: iso(-72 * 60), periodEnd: iso(-48 * 60), status: 'Retrying', attempts: 2, nextAttemptAt: iso(180), lastError: 'HTTP 503 Service Unavailable', reportJson: JSON.stringify({ 'organization-name': 'MailServer', 'report-id': 'preview-legacy-20260916', policies: [] }, null, 2) },
  { id: '8f529738-3a52-4a85-8f69-f1ea21643a67', policyDomain: 'broken-report.example', reportUri: 'https://tls.broken-report.example/ingest', periodStart: iso(-96 * 60), periodEnd: iso(-72 * 60), status: 'Failed', attempts: 5, nextAttemptAt: iso(-40 * 60), lastError: 'Name or service not known', reportJson: JSON.stringify({ 'organization-name': 'MailServer', 'report-id': 'preview-broken-20260915', policies: [] }, null, 2) },
]

export function enablePreviewMode() {
  previewMode = true
  if (import.meta.env.DEV) globalThis.sessionStorage.setItem(previewStorageKey, '1')
}
export function isPreviewMode() { return import.meta.env.DEV && previewMode }

function page<T>(items: T[], pageNumber: number, pageSize: number) {
  return { items: items.slice((pageNumber - 1) * pageSize, pageNumber * pageSize), page: pageNumber, pageSize, totalCount: items.length }
}

export async function mockApi<T>(path: string, init: RequestInit = {}): Promise<T> {
  if (path.split('?')[0] === '/quarantine/domains') return domains.map(domain => ({ id: domain.id, name: domain.name })) as T
  const url = new globalThis.URL(path, 'http://preview.local')
  const method = (init.method || 'GET').toUpperCase()
  if (url.pathname === '/firewall/rules') {
    if (method === 'POST') {
      const input = JSON.parse(String(init.body))
      const cidr = inspectFirewallInput(String(input.cidr ?? '')).normalized
      if (previewFirewall.some(rule => rule.cidr === cidr)) throw new Error('该网段的规则已存在')
      const rule = { id: globalThis.crypto.randomUUID(), cidr, note: String(input.note ?? '').trim(), isEnabled: true }
      previewFirewall.push(rule)
      return structuredClone(rule) as T
    }
    return structuredClone(previewFirewall) as T
  }
  const firewallMatch = url.pathname.match(/^\/firewall\/rules\/([^/]+)$/)
  if (firewallMatch) {
    const rule = previewFirewall.find(item => item.id === firewallMatch[1])
    if (!rule) throw new Error('规则不存在')
    if (method === 'DELETE') { previewFirewall = previewFirewall.filter(item => item.id !== rule.id); return undefined as T }
    if (method === 'PUT') {
      const input = JSON.parse(String(init.body)); rule.note = input.note; rule.isEnabled = input.isEnabled
      return structuredClone(rule) as T
    }
  }
  if (url.pathname === '/session/login' && method === 'POST') {
    const input = JSON.parse(String(init.body))
    if (input.userName !== 'preview-admin' || input.password !== 'preview-password') throw new Error('账号或密码不正确')
    previewSession = true
    return { id: 'preview-admin', userName: 'preview-admin', authenticated: true } as T
  }
  if (url.pathname === '/session/logout' && method === 'POST') { previewSession = false; return undefined as T }
  if (url.pathname === '/session/me') {
    if (!previewSession) throw new Error('请先登录')
    return { id: 'preview-admin', userName: 'preview-admin', authenticated: true } as T
  }
  if (url.pathname === '/session/permissions') return { fullAccess: true, systemPermissions: [], allDomainPermissions: [], domainPermissions: {} } as T
  if (url.pathname === '/permissions/catalog' && method === 'GET') return structuredClone(previewPermissionCatalog) as T
  if (url.pathname === '/tasks' && method === 'GET') return structuredClone(previewTasks) as T
  if (url.pathname === '/tasks/executions' && method === 'GET') {
    const take = Math.min(500, Math.max(1, Number(url.searchParams.get('take') || 100)))
    return structuredClone(previewTaskExecutions.slice(0, take)) as T
  }
  const taskMatch = url.pathname.match(/^\/tasks\/([^/]+)$/)
  if (taskMatch && method === 'PUT') {
    const task = previewTasks.find(item => item.id === taskMatch[1])
    if (!task) throw new Error('计划任务不存在')
    const input = JSON.parse(String(init.body)) as { enabled?: boolean; intervalMinutes?: number; timeoutSeconds?: number; batchSize?: number }
    const previous = { enabled: task.isEnabled, intervalMinutes: task.intervalMinutes, timeoutSeconds: task.timeoutSeconds, batchSize: task.batchSize }
    task.isEnabled = input.enabled !== false
    task.status = task.isEnabled ? 'Idle' : 'Paused'
    task.intervalMinutes = Math.min(525600, Math.max(1, Number(input.intervalMinutes || 1)))
    task.timeoutSeconds = Math.min(86400, Math.max(10, Number(input.timeoutSeconds || 10)))
    task.batchSize = Math.min(1000, Math.max(1, Number(input.batchSize || 1)))
    addPreviewAudit({
      content: '编辑后台任务', category: 'Update', status: 'Succeeded', actor: 'preview-admin', action: 'task.update', resource: `task:${task.type}`,
      detail: `更新 ${task.name} 的调度配置`, remoteAddress: '127.0.0.1', requestId: `req-task-${Date.now()}`,
      createdAt: new Date().toISOString(),
      changes: [
        { field: 'enabled', oldValue: String(previous.enabled), newValue: String(task.isEnabled) },
        { field: 'intervalMinutes', oldValue: String(previous.intervalMinutes), newValue: String(task.intervalMinutes) },
        { field: 'timeoutSeconds', oldValue: String(previous.timeoutSeconds), newValue: String(task.timeoutSeconds) },
        { field: 'batchSize', oldValue: String(previous.batchSize), newValue: String(task.batchSize) },
      ],
    })
    return undefined as T
  }
  const runTaskMatch = url.pathname.match(/^\/tasks\/([^/]+)\/run$/)
  if (runTaskMatch && method === 'POST') {
    const task = previewTasks.find(item => item.id === runTaskMatch[1])
    if (!task) throw new Error('计划任务不存在')
    if (task.status === 'Running') throw new Error('任务正在运行，不能重复触发')
    const startedAt = new Date()
    const executionId = globalThis.crypto.randomUUID()
    const processedCount = task.type === 'cleanup.recycle' ? 6 : 128
    const releasedBytes = task.type.includes('cleanup') ? 48 * 1024 * 1024 : 0
    previewTaskExecutions = [{
      id: executionId, scheduledTaskId: task.id, status: 'Succeeded',
      startedAt: startedAt.toISOString(), completedAt: new Date(startedAt.getTime() + 2400).toISOString(),
      processedCount, releasedBytes, attempt: 1,
    }, ...previewTaskExecutions]
    task.status = task.isEnabled ? 'Idle' : 'Paused'
    task.lastRunAt = startedAt.toISOString()
    task.nextRunAt = new Date(startedAt.getTime() + task.intervalMinutes * 60_000).toISOString()
    addPreviewAudit({
      content: '执行后台任务', category: 'BackgroundTask', status: 'Succeeded', actor: 'preview-admin', action: 'task.run', resource: `task:${task.type}`,
      detail: `${task.name} 手动执行完成，处理 ${processedCount} 项`, remoteAddress: '127.0.0.1',
      requestId: `req-task-${Date.now()}`, relatedId: executionId, durationMilliseconds: 2400, createdAt: startedAt.toISOString(), changes: [],
    })
    return { executionId } as T
  }
  if (url.pathname === '/audit' && method === 'GET') {
    const search = url.searchParams.get('search')?.trim().toLowerCase()
    const category = url.searchParams.get('category')
    const status = url.searchParams.get('status')
    const from = url.searchParams.get('from') ? Date.parse(url.searchParams.get('from')!) : null
    const to = url.searchParams.get('to') ? Date.parse(url.searchParams.get('to')!) : null
    const filtered = previewAuditEntries.filter(entry => {
      const haystack = `${entry.number} ${entry.content} ${entry.actor} ${entry.action} ${entry.resource} ${entry.detail ?? ''} ${entry.requestId ?? ''}`.toLowerCase()
      const createdAt = Date.parse(entry.createdAt)
      return (!search || haystack.includes(search))
        && (!category || entry.category === category)
        && (!status || entry.status === status)
        && (from === null || createdAt >= from)
        && (to === null || createdAt <= to)
    }).map(entry => ({
      id: entry.id, number: entry.number, content: entry.content, category: entry.category, status: entry.status,
      actor: entry.actor, requestId: entry.requestId, pluginId: entry.pluginId,
      pluginName: entry.pluginName, createdAt: entry.createdAt,
    }))
    return page(filtered, Number(url.searchParams.get('page') || 1), Number(url.searchParams.get('pageSize') || 20)) as T
  }
  const auditDetailMatch = url.pathname.match(/^\/audit\/([^/]+)$/)
  if (auditDetailMatch && method === 'GET') {
    const entry = previewAuditEntries.find(item => item.id === auditDetailMatch[1])
    if (!entry) throw new Error('审计记录不存在')
    return structuredClone(entry) as T
  }
  if (url.pathname === '/admins') {
    if (method === 'GET') {
      const search = url.searchParams.get('search')?.trim().toLowerCase()
      const status = url.searchParams.get('status')?.trim().toLowerCase()
      const filtered = previewAdmins.filter(admin =>
        (!search || admin.userName.toLowerCase().includes(search))
        && (!status || status === 'any' || (status === 'enabled' ? admin.isEnabled : !admin.isEnabled)))
      return page(filtered, Number(url.searchParams.get('page') || 1), Number(url.searchParams.get('pageSize') || 20)) as T
    }
    if (method === 'POST') {
      const input = JSON.parse(String(init.body)) as { userName?: string; password?: string; isEnabled?: boolean; grants?: MockPermissionGrant[] }
      const userName = input.userName?.trim().toLowerCase() ?? ''
      if (!userName) throw new Error('管理员用户名不能为空')
      if ((input.password?.length ?? 0) < 12) throw new Error('密码至少需要 12 个字符')
      if (!input.grants?.length) throw new Error('至少需要授予一项权限')
      if (previewAdmins.some(admin => admin.userName.toLowerCase() === userName)) throw new Error('管理员用户名已存在')
      const admin: MockAdmin = {
        id: globalThis.crypto.randomUUID(),
        userName,
        isEnabled: input.isEnabled !== false,
        createdAt: new Date().toISOString(),
        isPrimary: false,
      }
      previewAdmins = [admin, ...previewAdmins]
      previewAdminGrants[admin.id] = structuredClone(input.grants)
      return structuredClone(admin) as T
    }
  }
  const adminGrantMatch = url.pathname.match(/^\/admins\/([^/]+)\/grants$/)
  if (adminGrantMatch) {
    const admin = previewAdmins.find(item => item.id === adminGrantMatch[1])
    if (!admin) throw new Error('管理员不存在')
    if (method === 'GET') return structuredClone(previewAdminGrants[admin.id] ?? []) as T
    if (method === 'PUT') {
      if (admin.isPrimary) throw new Error('主管理员始终拥有全部权限，不能修改授权')
      const input = JSON.parse(String(init.body)) as { grants?: MockPermissionGrant[] }
      if (!input.grants?.length) throw new Error('至少需要保留一项权限')
      previewAdminGrants[admin.id] = structuredClone(input.grants)
      return structuredClone(previewAdminGrants[admin.id]) as T
    }
  }
  const adminMatch = url.pathname.match(/^\/admins\/([^/]+)$/)
  if (adminMatch) {
    const admin = previewAdmins.find(item => item.id === adminMatch[1])
    if (!admin) throw new Error('管理员不存在')
    if (method === 'PATCH') {
      const input = JSON.parse(String(init.body)) as { userName?: string; isEnabled?: boolean; password?: string | null }
      const userName = input.userName?.trim().toLowerCase() ?? ''
      if (!userName) throw new Error('管理员用户名不能为空')
      if (input.password && input.password.length < 12) throw new Error('密码至少需要 12 个字符')
      if (previewAdmins.some(item => item.id !== admin.id && item.userName.toLowerCase() === userName)) throw new Error('管理员用户名已存在')
      if (admin.isPrimary && (userName !== admin.userName || input.isEnabled === false)) throw new Error('主管理员的用户名和启用状态不能修改')
      admin.userName = userName
      admin.isEnabled = input.isEnabled !== false
      return structuredClone(admin) as T
    }
    if (method === 'DELETE') {
      if (admin.isPrimary) throw new Error('主管理员不能删除')
      previewAdmins = previewAdmins.filter(item => item.id !== admin.id)
      delete previewAdminGrants[admin.id]
      return undefined as T
    }
  }
  if (url.pathname === '/api-keys') {
    if (method === 'GET') return structuredClone(previewApiKeys) as T
    if (method === 'POST') {
      const input = JSON.parse(String(init.body)) as { name?: string; expiresAt?: string | null; grants?: MockPermissionGrant[] }
      const name = input.name?.trim() ?? ''
      if (!name) throw new Error('API Key 名称不能为空')
      if (!input.grants?.length) throw new Error('至少需要授予一项权限')
      if (previewApiKeys.some(item => item.name.toLowerCase() === name.toLowerCase())) throw new Error('API Key 名称已存在')
      if (input.expiresAt && (!Number.isFinite(Date.parse(input.expiresAt)) || Date.parse(input.expiresAt) <= Date.now())) {
        throw new Error('过期时间必须晚于当前时间')
      }
      const secret = `msk_preview_${globalThis.crypto.randomUUID().replaceAll('-', '')}`
      const key: MockApiKey = {
        id: globalThis.crypto.randomUUID(),
        name,
        prefix: secret.slice(0, 16),
        createdBy: 'preview-admin',
        createdAt: new Date().toISOString(),
        ...(input.expiresAt ? { expiresAt: new Date(input.expiresAt).toISOString() } : {}),
      }
      previewApiKeys = [key, ...previewApiKeys]
      previewApiKeyGrants[key.id] = structuredClone(input.grants)
      return { id: key.id, apiKey: secret, expiresAt: key.expiresAt ?? null } as T
    }
  }
  const apiKeyGrantMatch = url.pathname.match(/^\/api-keys\/([^/]+)\/grants$/)
  if (apiKeyGrantMatch) {
    const key = previewApiKeys.find(item => item.id === apiKeyGrantMatch[1])
    if (!key) throw new Error('API Key 不存在')
    if (method === 'GET') return structuredClone(previewApiKeyGrants[key.id] ?? []) as T
    if (method === 'PUT') {
      const input = JSON.parse(String(init.body)) as { grants?: MockPermissionGrant[] }
      if (!input.grants?.length) throw new Error('至少需要保留一项权限')
      previewApiKeyGrants[key.id] = structuredClone(input.grants)
      return structuredClone(previewApiKeyGrants[key.id]) as T
    }
  }
  const apiKeyRefreshMatch = url.pathname.match(/^\/api-keys\/([^/]+)\/refresh$/)
  if (apiKeyRefreshMatch && method === 'POST') {
    const key = previewApiKeys.find(item => item.id === apiKeyRefreshMatch[1])
    if (!key) throw new Error('API Key 不存在')
    if (key.revokedAt) throw new Error('已吊销的 API Key 不能轮换')
    const secret = `msk_preview_${globalThis.crypto.randomUUID().replaceAll('-', '')}`
    key.prefix = secret.slice(0, 16)
    key.lastUsedAt = undefined
    return { id: key.id, apiKey: secret, expiresAt: key.expiresAt ?? null } as T
  }
  const apiKeyMatch = url.pathname.match(/^\/api-keys\/([^/]+)$/)
  if (apiKeyMatch && method === 'DELETE') {
    const key = previewApiKeys.find(item => item.id === apiKeyMatch[1])
    if (!key) throw new Error('API Key 不存在')
    previewApiKeys = previewApiKeys.filter(item => item.id !== key.id)
    delete previewApiKeyGrants[key.id]
    return undefined as T
  }
  if (previewSettings[url.pathname]) {
    if (method === 'PUT') {
      const input = JSON.parse(String(init.body)) as Record<string, unknown>
      if (url.pathname === '/settings/message-templates') {
        const senderName = String(input.senderDisplayName ?? '').trim()
        const permanentSubject = String(input.permanentFailureSubject ?? '').trim()
        const retrySubject = String(input.retryExhaustedSubject ?? '').trim()
        const permanentBody = String(input.permanentFailureBody ?? '').trim()
        const retryBody = String(input.retryExhaustedBody ?? '').trim()
        if (senderName.length < 1 || senderName.length > 120) throw new Error('发件人显示名称必须为 1 至 120 个字符')
        if (permanentSubject.length < 1 || permanentSubject.length > 200 || retrySubject.length < 1 || retrySubject.length > 200) throw new Error('模板主题必须为 1 至 200 个字符')
        if (permanentBody.length < 1 || permanentBody.length > 8000 || retryBody.length < 1 || retryBody.length > 8000) throw new Error('消息模板正文必须为 1 至 8000 个字符')
        previewSettings[url.pathname] = {
          senderDisplayName: senderName,
          permanentFailureSubject: permanentSubject,
          permanentFailureBody: permanentBody,
          retryExhaustedSubject: retrySubject,
          retryExhaustedBody: retryBody,
        }
        addPreviewAudit({
          content: '更新退信消息模板', category: 'Update', status: 'Succeeded', actor: 'preview-admin', action: 'settings.message-templates.update', resource: 'settings:message-templates',
          detail: '更新永久失败与重试耗尽模板', remoteAddress: '127.0.0.1', requestId: `req-template-${Date.now()}`,
          createdAt: new Date().toISOString(), changes: [],
        })
      } else if (url.pathname === '/settings/delivery') {
        const batchSize = Number(input.batchSize)
        const leaseSeconds = Number(input.leaseSeconds)
        const maximumAttempts = Number(input.maximumAttempts)
        const pollIntervalSeconds = Number(input.pollIntervalSeconds)
        if (!Number.isInteger(batchSize) || batchSize < 1 || batchSize > 200) throw new Error('每批投递数量必须为 1 至 200 的整数')
        if (!Number.isInteger(leaseSeconds) || leaseSeconds < 10 || leaseSeconds > 3600) throw new Error('租约时长必须为 10 至 3600 秒')
        if (!Number.isInteger(maximumAttempts) || maximumAttempts < 1 || maximumAttempts > 100) throw new Error('最大尝试次数必须为 1 至 100')
        if (!Number.isInteger(pollIntervalSeconds) || pollIntervalSeconds < 1 || pollIntervalSeconds > 300) throw new Error('轮询间隔必须为 1 至 300 秒')
        previewSettings[url.pathname] = { batchSize, leaseSeconds, maximumAttempts, pollIntervalSeconds }
        addPreviewAudit({
          content: '更新投递调度设置', category: 'Update', status: 'Succeeded', actor: 'preview-admin', action: 'settings.delivery.update', resource: 'settings:delivery',
          detail: `batch=${batchSize};lease=${leaseSeconds};attempts=${maximumAttempts};poll=${pollIntervalSeconds}`,
          remoteAddress: '127.0.0.1', requestId: `req-settings-${Date.now()}`, createdAt: new Date().toISOString(), changes: [],
        })
      } else if (url.pathname === '/settings/smtp') {
        const commandTimeoutSeconds = Number(input.commandTimeoutSeconds)
        if (!Number.isInteger(commandTimeoutSeconds) || commandTimeoutSeconds < 5 || commandTimeoutSeconds > 3600) throw new Error('SMTP 命令超时必须为 5 至 3600 秒')
        previewSettings[url.pathname] = { commandTimeoutSeconds }
        addPreviewAudit({
          content: '更新 SMTP 设置', category: 'Update', status: 'Succeeded', actor: 'preview-admin', action: 'settings.smtp.update', resource: 'settings:smtp',
          detail: `timeout=${commandTimeoutSeconds}`, remoteAddress: '127.0.0.1', requestId: `req-settings-${Date.now()}`,
          createdAt: new Date().toISOString(), changes: [],
        })
      } else if (url.pathname === '/settings/mailboxes') {
        const systemFolders = ['INBOX', 'Sent', 'Drafts', 'Trash', 'Junk', 'Archives']
        const requested = Array.isArray(input.defaultFolders) ? input.defaultFolders.map(value => String(value).trim().replace(/^"|"$/g, '')) : []
        if (requested.some(folder => !folder || folder.includes('..') || folder.length > 255)) throw new Error('邮箱文件夹名称无效')
        const defaultFolders = [...systemFolders]
        for (const folder of requested) {
          if (!defaultFolders.some(current => current.toLowerCase() === folder.toLowerCase())) defaultFolders.push(folder)
        }
        previewSettings[url.pathname] = { defaultFolders }
        addPreviewAudit({
          content: '更新默认邮箱文件夹', category: 'Update', status: 'Succeeded', actor: 'preview-admin', action: 'settings.mailboxes.update', resource: 'settings:mailboxes',
          detail: defaultFolders.join(','), remoteAddress: '127.0.0.1', requestId: `req-settings-${Date.now()}`,
          createdAt: new Date().toISOString(), changes: [],
        })
      } else if (url.pathname === '/settings/certificates') {
        const current = previewSettings[url.pathname]!
        const value = String(input.defaultMailHostName ?? '').trim().replace(/\.+$/, '').toLowerCase()
        if (value && (value.length > 255 || value.includes(' ') || value.includes('..') || /[^a-z0-9.-]/.test(value))) throw new Error('默认邮件主机名格式无效')
        const availableMailHostNames = structuredClone(current.availableMailHostNames ?? []) as string[]
        const allowSingleCertificateFallback = input.allowSingleCertificateFallback === true
        const effectiveDefaultMailHostName = value && availableMailHostNames.some(host => host.toLowerCase() === value)
          ? value
          : allowSingleCertificateFallback && availableMailHostNames.length === 1 ? availableMailHostNames[0]! : null
        previewSettings[url.pathname] = {
          defaultMailHostName: value || null,
          allowSingleCertificateFallback,
          availableMailHostNames,
          effectiveDefaultMailHostName,
        }
        addPreviewAudit({
          content: '更新证书选择设置', category: 'Update', status: 'Succeeded', actor: 'preview-admin', action: 'settings.certificates.update', resource: 'settings:certificates',
          detail: `${value || 'auto'};fallback=${allowSingleCertificateFallback}`, remoteAddress: '127.0.0.1', requestId: `req-settings-${Date.now()}`,
          createdAt: new Date().toISOString(), changes: [],
        })
      } else {
        previewSettings[url.pathname] = input
      }
    }
    return structuredClone(previewSettings[url.pathname]) as T
  }
  if (url.pathname === '/plugins' && method === 'GET') return structuredClone(previewPlugins) as T
  if (url.pathname === '/plugins/order' && method === 'PUT') {
    const ids = JSON.parse(String(init.body)).pluginIds as string[]
    if (ids.length !== previewPlugins.length || new Set(ids).size !== ids.length || ids.some(id => !previewPlugins.some(plugin => plugin.id === id))) {
      throw new Error('插件顺序包含未知、重复或缺失的插件')
    }
    previewPlugins.sort((a,b) => ids.indexOf(a.id) - ids.indexOf(b.id))
    previewPlugins.forEach((plugin, index) => { plugin.effectiveOrder = (index + 1) * 10 })
    addPreviewAudit({
      content: '调整插件执行顺序', category: 'Plugin', status: 'Succeeded', actor: 'preview-admin', action: 'plugin.order.update', resource: 'plugins:order',
      detail: ids.join(','), remoteAddress: '127.0.0.1', requestId: `req-plugin-${Date.now()}`,
      createdAt: new Date().toISOString(), changes: [],
    })
    return structuredClone(previewPlugins) as T
  }
  const pluginConfigurationMatch = url.pathname.match(/^\/plugins\/([^/]+)\/configuration$/)
  if (pluginConfigurationMatch) {
    const pluginId = decodeURIComponent(pluginConfigurationMatch[1]!)
    const plugin = previewPlugins.find(item => item.id === pluginId)
    const configuration = previewPluginConfigurations[pluginId]
    if (!plugin || !configuration) throw new Error('插件不存在')
    if (method === 'PUT') {
      const input = JSON.parse(String(init.body)) as { enabled?: boolean; values?: Record<string, unknown>; clearedSecrets?: string[] }
      const definitions = new Map(configuration.definitions.map(definition => [definition.key, definition]))
      const values = input.values ?? {}
      if (Object.keys(values).some(key => !definitions.has(key))) throw new Error('插件配置包含未知字段')
      for (const [key, value] of Object.entries(values)) {
        const definition = definitions.get(key)!
        if (definition.type === 'Integer') {
          const number = Number(value)
          if (!Number.isInteger(number) || number < (definition.minimum ?? Number.MIN_SAFE_INTEGER) || number > (definition.maximum ?? Number.MAX_SAFE_INTEGER)) {
            throw new Error(`${definition.label}超出允许范围`)
          }
        }
        if (definition.type === 'Select' && !definition.options.some(option => option.value === value)) throw new Error(`${definition.label}的选项无效`)
        if (definition.required && definition.type === 'Text' && !String(value ?? '').trim()) throw new Error(`${definition.label}不能为空`)
      }
      plugin.enabled = input.enabled !== false
      for (const setting of configuration.values) {
        const definition = definitions.get(setting.key)!
        if (definition.type === 'Password') {
          if (input.clearedSecrets?.includes(setting.key)) setting.hasSecret = false
          if (typeof values[setting.key] === 'string' && values[setting.key]) setting.hasSecret = true
          setting.value = null
        } else if (Object.hasOwn(values, setting.key)) {
          setting.value = structuredClone(values[setting.key]) as typeof setting.value
        }
      }
      addPreviewAudit({
        content: '更新插件配置', category: 'Plugin', status: 'Succeeded', actor: 'preview-admin', action: 'plugin.settings.update', resource: `plugin:${plugin.id}`,
        detail: plugin.displayName, remoteAddress: '127.0.0.1', requestId: `req-plugin-${Date.now()}`,
        pluginId: plugin.id, pluginName: plugin.displayName, createdAt: new Date().toISOString(),
        changes: Object.keys(values).map(key => ({ field: key, newValue: definitions.get(key)?.type === 'Password' ? '***' : JSON.stringify(values[key]) })),
        rawJson: JSON.stringify({ pluginId: plugin.id, enabled: plugin.enabled, changedFields: Object.keys(values), secret: '***' }, null, 2),
      })
    }
    return { plugin: structuredClone(plugin), definitions: structuredClone(configuration.definitions), values: structuredClone(configuration.values) } as T
  }
  if (url.pathname === '/recycle-bin' && method === 'GET') return structuredClone(previewRecycle) as T
  const recycleMatch = url.pathname.match(/^\/recycle-bin\/([^/]+)(\/restore)?$/)
  if (recycleMatch) {
    const queueId = recycleMatch[1]!
    const restoring = Boolean(recycleMatch[2])
    const item = previewRecycle.find(candidate => candidate.queueId === queueId)
    if (!item) throw new Error('回收站项目不存在或已被处理')
    if ((restoring && method !== 'POST') || (!restoring && method !== 'DELETE')) throw new Error('不支持的回收站操作')
    if (restoring && (item.resourceType === 'ContentFile' || !item.resourceId)) throw new Error('该资源不支持恢复，只能永久清理')

    previewRecycle = previewRecycle.filter(candidate => candidate.queueId !== queueId)
    addPreviewAudit({
      content: restoring ? '恢复回收站对象' : '永久清理回收站对象',
      category: restoring ? 'Update' : 'Delete', status: 'Succeeded', actor: 'preview-admin',
      action: restoring ? 'recycle.restore' : 'recycle.purge', resource: `cleanup:${queueId}`,
      detail: `${item.resourceType}: ${item.displayName}; estimatedBytes=${item.estimatedBytes}`,
      remoteAddress: '127.0.0.1', requestId: `req-recycle-${Date.now()}`,
      relatedId: item.resourceId ?? undefined, createdAt: new Date().toISOString(), changes: [],
    })
    return undefined as T
  }

  if (url.pathname === '/overview') {
    return {
      domains: domains.length,
      accounts: domains.reduce((total, domain) => total + domain.accountCount, 0),
      aliases: 47,
      pendingDeliveries: deliveryJobs.filter(job => job.status === 'Pending' || job.status === 'Deferred').length,
      failedDeliveries: deliveryJobs.filter(job => job.status === 'Failed').length,
      storedBytes: domains.reduce((total, domain) => total + domain.usedBytes, 0),
      directMxHealthyDomains: domains.filter(domain => domain.isActive && domain.dnsStatus === 'Healthy').length,
      directMxAttentionDomains: domains.filter(domain => domain.isActive && domain.dnsStatus === 'Unhealthy').length,
      directMxUncheckedDomains: domains.filter(domain => domain.isActive && domain.dnsStatus === 'PendingConfiguration').length,
      expiringCertificates: domains.filter(domain => domain.certificateNotAfter?.startsWith('2026-09')).length,
      deliveredLast24Hours: deliveryJobs.filter(job => job.status === 'Delivered').length,
      failedLast24Hours: deliveryJobs.filter(job => job.status === 'Failed').length,
    } as T
  }

  if (url.pathname === '/domains/search') {
    const search = url.searchParams.get('search')?.toLowerCase()
    const active = url.searchParams.get('isActive')
    const dnsStatus = url.searchParams.get('dnsStatus')
    const filtered = domains.filter(domain =>
      (!search || domain.name.includes(search) || domain.mailHostName.includes(search))
      && (active === null || domain.isActive === (active === 'true'))
      && (!dnsStatus || domain.dnsStatus === dnsStatus))
    return page(filtered, Number(url.searchParams.get('page') || 1), Number(url.searchParams.get('pageSize') || 20)) as T
  }

  if (url.pathname === '/domains' && method === 'POST') {
    const request = JSON.parse(String(init.body)) as { name: string }
    const domain: MockDomain = { id: globalThis.crypto.randomUUID(), name: request.name, mailHostName: `mail.${request.name}`, isActive: true, dnsStatus: 'PendingConfiguration', hasCertificate: false, accountCount: 0, quotaBytes: 107374182400, usedBytes: 0, createdAt: new Date().toISOString() }
    domains.unshift(domain)
    return domain as T
  }

  if (url.pathname === '/domains/batch/dns-check' && method === 'POST') {
    const request = JSON.parse(String(init.body)) as { domainIds: string[] }
    domains.filter(domain => request.domainIds.includes(domain.id)).forEach(domain => {
      domain.dnsStatus = 'Healthy'
      domain.dnsCheckedAt = new Date().toISOString()
      previewDnsChecks[domain.id] = createDnsCheck(domain)
    })
    return request.domainIds.map(id => previewDnsChecks[id]).filter(Boolean) as T
  }

  const domainDetailMatch = url.pathname.match(/^\/domains\/([^/]+)$/)
  if (domainDetailMatch && method === 'GET') {
    const domain = domains.find(candidate => candidate.id === domainDetailMatch[1])
    if (!domain) throw new Error('邮件域不存在')
    const selector = previewDkim[domain.id]?.selector ?? 'mail'
    return {
      domain: {
        id: domain.id, name: domain.name, mailHostName: domain.mailHostName, smtpHostName: domain.mailHostName,
        outboundIpAddresses: '20.24.67.236', isActive: domain.isActive, quotaBytes: domain.quotaBytes,
        usedBytes: domain.usedBytes, directDeliverSameDomain: true, dkimSelector: selector,
        dkimPublicKey: previewDkim[domain.id]?.publicKey, createdAt: domain.createdAt,
      },
      certificate: domain.hasCertificate ? structuredClone(previewCertificates[domain.id] ?? createPreviewCertificate(domain)) : null,
      dnsSettings: structuredClone(previewDnsSettings[domain.id] ?? createPreviewDnsSettings(domain)),
      connections: [
        { protocol: 'SMTP submission', hostName: domain.mailHostName, port: 587, security: 'StartTls', authenticationRequired: true, userNameFormat: 'EmailAddress', description: '客户端发信' },
        { protocol: 'IMAP', hostName: domain.mailHostName, port: 993, security: 'ImplicitTls', authenticationRequired: true, userNameFormat: 'EmailAddress', description: '邮箱同步' },
      ],
      outboundAddressOptions: [
        { address: '20.24.67.236', interfaceName: 'eth0', addressFamily: 'IPv4' },
        { address: '2001:db8:1::25', interfaceName: 'eth0', addressFamily: 'IPv6' },
      ],
    } as T
  }

  const domainHealthMatch = url.pathname.match(/^\/domains\/([^/]+)\/health$/)
  if (domainHealthMatch && method === 'GET') {
    const domain = domains.find(candidate => candidate.id === domainHealthMatch[1])
    if (!domain) throw new Error('邮件域不存在')
    const accounts = domainAccounts[domain.id] ?? []
    const aliases = domainAliases[domain.id] ?? []
    return {
      accountCount: Math.max(domain.accountCount, accounts.length), enabledAccountCount: accounts.length ? accounts.filter(account => account.isEnabled).length : domain.accountCount,
      aliasCount: Math.max(aliases.length, domain.id.endsWith('401') ? 47 : aliases.length), pendingDeliveries: domain.id.endsWith('401') ? 18 : 4,
      failedDeliveries: domain.id.endsWith('402') ? 5 : 1, hasCertificate: domain.hasCertificate,
      certificateNotAfter: domain.certificateNotAfter ?? null, hasDkim: true, lastAuditAt: '2026-09-18T04:20:08Z', pendingQuarantineCount: 3,
    } as T
  }

  const domainAccountsMatch = url.pathname.match(/^\/domains\/([^/]+)\/accounts\/search$/)
  if (domainAccountsMatch && method === 'GET') {
    const domain = domains.find(candidate => candidate.id === domainAccountsMatch[1])
    if (!domain) throw new Error('邮件域不存在')
    const query = url.searchParams.get('query')?.trim().toLowerCase()
    const enabled = url.searchParams.get('isEnabled')
    const rows = (domainAccounts[domain.id] ?? []).filter(account => (!query || account.address.toLowerCase().includes(query))
      && (enabled === null || account.isEnabled === (enabled === 'true')))
    return page(rows, Number(url.searchParams.get('page') || 1), Number(url.searchParams.get('pageSize') || 10)) as T
  }

  const domainAccountListMatch = url.pathname.match(/^\/domains\/([^/]+)\/accounts$/)
  if (domainAccountListMatch && method === 'GET') {
    const domain = domains.find(candidate => candidate.id === domainAccountListMatch[1])
    if (!domain) throw new Error('邮件域不存在')
    // 与真实 API 一致：按启用优先、地址升序返回全量账号，供别名目标选择器使用。
    return structuredClone((domainAccounts[domain.id] ?? [])
      .slice()
      .sort((left, right) => (Number(right.isEnabled) - Number(left.isEnabled)) || left.address.localeCompare(right.address))) as T
  }

  const createDomainAccountMatch = url.pathname.match(/^\/domains\/([^/]+)\/accounts$/)
  if (createDomainAccountMatch && method === 'POST') {
    const domain = domains.find(candidate => candidate.id === createDomainAccountMatch[1])
    if (!domain) throw new Error('邮件域不存在')
    const request = JSON.parse(String(init.body)) as { localPart: string; password: string; quotaBytes?: number | null }
    const localPart = request.localPart.trim().toLowerCase()
    if (!/^[a-z0-9.!#$%&'*+/=?^_`{|}~-]+$/.test(localPart)) throw new Error('邮箱本地部分格式无效')
    if (request.password.length < 12) throw new Error('密码至少需要 12 个字符')
    const rows = domainAccounts[domain.id] ??= []
    const address = `${localPart}@${domain.name}`
    if (rows.some(account => account.address === address)) throw new Error('邮箱账号已存在')
    const account: MockDomainAccount = {
      id: globalThis.crypto.randomUUID(), address, isEnabled: true, quotaBytes: request.quotaBytes ?? 10_737_418_240,
      usedBytes: 0, createdAt: new Date().toISOString(), aliasCount: 0,
    }
    rows.unshift(account)
    domain.accountCount++
    addPreviewAudit({ content: '创建邮箱账号', category: 'Create', status: 'Succeeded', actor: 'preview-admin', action: 'account.create', resource: `account:${account.id}`, detail: address, remoteAddress: '127.0.0.1', requestId: `req-account-${Date.now()}`, relatedId: domain.id, createdAt: new Date().toISOString(), changes: [] })
    return structuredClone(account) as T
  }

  const domainAliasesMatch = url.pathname.match(/^\/domains\/([^/]+)\/aliases\/search$/)
  if (domainAliasesMatch && method === 'GET') {
    const domain = domains.find(candidate => candidate.id === domainAliasesMatch[1])
    if (!domain) throw new Error('邮件域不存在')
    const query = url.searchParams.get('query')?.trim().toLowerCase()
    const rows = (domainAliases[domain.id] ?? []).filter(alias => !query || `${alias.source} ${alias.targetAddress ?? ''}`.toLowerCase().includes(query))
    return page(rows, Number(url.searchParams.get('page') || 1), Number(url.searchParams.get('pageSize') || 10)) as T
  }

  const domainRulesMatch = url.pathname.match(/^\/domains\/([^/]+)\/rules$/)
  if (domainRulesMatch && method === 'GET') {
    if (!domains.some(candidate => candidate.id === domainRulesMatch[1])) throw new Error('邮件域不存在')
    const direction = url.searchParams.get('direction') ?? 'Incoming'
    return structuredClone((domainRules[domainRulesMatch[1]!] ?? [])
      .filter(rule => rule.direction === direction)
      .sort((a, b) => a.priority - b.priority)) as T
  }

  const createDomainAliasMatch = url.pathname.match(/^\/domains\/([^/]+)\/aliases$/)
  if (createDomainAliasMatch && method === 'POST') {
    const domain = domains.find(candidate => candidate.id === createDomainAliasMatch[1])
    if (!domain) throw new Error('邮件域不存在')
    const request = JSON.parse(String(init.body)) as { sourceLocalPart: string; targetAccountId: string; mode: MockDomainAlias['mode'] }
    const account = (domainAccounts[domain.id] ?? []).find(candidate => candidate.id === request.targetAccountId)
    if (!account) throw new Error('目标邮箱账号不存在')
    const source = `${request.sourceLocalPart.trim().toLowerCase()}@${domain.name}`
    const rows = domainAliases[domain.id] ??= []
    if (rows.some(alias => alias.source === source)) throw new Error('邮箱别名已存在')
    const alias: MockDomainAlias = { id: globalThis.crypto.randomUUID(), source, targetAccountId: account.id, targetAddress: account.address, isEnabled: true, legacyDestinationAddress: null, mode: request.mode, canReceive: true, canSend: request.mode === 'SendReceive' }
    rows.unshift(alias)
    account.aliasCount++
    addPreviewAudit({ content: '创建邮箱别名', category: 'Create', status: 'Succeeded', actor: 'preview-admin', action: 'alias.create', resource: `alias:${alias.id}`, detail: `${source} -> ${account.address}`, remoteAddress: '127.0.0.1', requestId: `req-alias-${Date.now()}`, relatedId: domain.id, createdAt: new Date().toISOString(), changes: [] })
    return structuredClone(alias) as T
  }

  const inspectUploadMatch = url.pathname.match(/^\/domains\/([^/]+)\/certificate\/inspect-upload$/)
  if (inspectUploadMatch && method === 'POST') {
    const domain = domains.find(candidate => candidate.id === inspectUploadMatch[1])
    if (!domain) throw new Error('邮件域不存在')
    const upload = await readCertificateUpload(init)
    return inspectPreviewCertificate(domain, upload.certificatePem, upload.privateKeyPem, upload.password) as T
  }

  const uploadCertificateMatch = url.pathname.match(/^\/domains\/([^/]+)\/certificate\/upload$/)
  if (uploadCertificateMatch && method === 'POST') {
    const domain = domains.find(candidate => candidate.id === uploadCertificateMatch[1])
    if (!domain) throw new Error('邮件域不存在')
    const upload = await readCertificateUpload(init)
    const inspection = inspectPreviewCertificate(domain, upload.certificatePem, upload.privateKeyPem, upload.password)
    const record = savePreviewCertificate(domain, 'ManagedUpload', `managed://${domain.mailHostName}/fullchain.pem`, `managed://${domain.mailHostName}/private.key`, inspection)
    addPreviewAudit({ content: '上传并启用 TLS 证书', category: 'Update', status: 'Succeeded', actor: 'preview-admin', action: 'domain.certificate.upload', resource: `domain:${domain.id}`, detail: domain.mailHostName, remoteAddress: '127.0.0.1', requestId: `req-certificate-${Date.now()}`, relatedId: domain.id, createdAt: new Date().toISOString(), changes: [] })
    return structuredClone(record) as T
  }

  const inspectPathMatch = url.pathname.match(/^\/domains\/([^/]+)\/certificate\/inspect-path$/)
  if (inspectPathMatch && method === 'POST') {
    const domain = domains.find(candidate => candidate.id === inspectPathMatch[1])
    if (!domain) throw new Error('邮件域不存在')
    const request = JSON.parse(String(init.body)) as { certificatePath: string; privateKeyPath: string }
    requirePreviewAbsolutePath(request.certificatePath, '证书链')
    requirePreviewAbsolutePath(request.privateKeyPath, '私钥')
    return {
      subject: `CN=${domain.mailHostName}`, issuer: 'Preview External CA', subjectAlternativeNames: [domain.mailHostName],
      thumbprint: hashPreview(request.certificatePath + request.privateKeyPath).padEnd(32, '0').slice(0, 32),
      notBefore: '2026-08-01T00:00:00Z', notAfter: '2027-08-01T00:00:00Z',
      hasMatchingPrivateKey: true, supportsServerAuthentication: true, coversMailHostName: true,
    } as T
  }

  const bindPathMatch = url.pathname.match(/^\/domains\/([^/]+)\/certificate\/path$/)
  if (bindPathMatch && method === 'PUT') {
    const domain = domains.find(candidate => candidate.id === bindPathMatch[1])
    if (!domain) throw new Error('邮件域不存在')
    const request = JSON.parse(String(init.body)) as { certificatePath: string; privateKeyPath: string }
    const certificatePath = requirePreviewAbsolutePath(request.certificatePath, '证书链')
    const privateKeyPath = requirePreviewAbsolutePath(request.privateKeyPath, '私钥')
    const record = savePreviewCertificate(domain, 'ExternalPath', certificatePath, privateKeyPath, {
      subject: `CN=${domain.mailHostName}`, issuer: 'Preview External CA', subjectAlternativeNames: [domain.mailHostName],
      thumbprint: hashPreview(certificatePath + privateKeyPath).padEnd(32, '0').slice(0, 32),
      notBefore: '2026-08-01T00:00:00Z', notAfter: '2027-08-01T00:00:00Z',
      hasMatchingPrivateKey: true, supportsServerAuthentication: true, coversMailHostName: true,
    })
    addPreviewAudit({ content: '绑定服务器路径 TLS 证书', category: 'Update', status: 'Succeeded', actor: 'preview-admin', action: 'domain.certificate.bind-path', resource: `domain:${domain.id}`, detail: certificatePath, remoteAddress: '127.0.0.1', requestId: `req-certificate-${Date.now()}`, relatedId: domain.id, createdAt: new Date().toISOString(), changes: [] })
    return structuredClone(record) as T
  }

  const deleteCertificateMatch = url.pathname.match(/^\/domains\/([^/]+)\/certificate$/)
  if (deleteCertificateMatch && method === 'DELETE') {
    const domain = domains.find(candidate => candidate.id === deleteCertificateMatch[1])
    if (!domain) throw new Error('邮件域不存在')
    delete previewCertificates[domain.id]
    domain.hasCertificate = false
    domain.certificateNotAfter = undefined
    const settings = previewSettings['/settings/certificates']
    if (settings) {
      settings.availableMailHostNames = ((settings.availableMailHostNames as string[] | undefined) ?? []).filter(host => host !== domain.mailHostName)
      if (settings.effectiveDefaultMailHostName === domain.mailHostName) settings.effectiveDefaultMailHostName = null
    }
    addPreviewAudit({ content: '删除 TLS 证书', category: 'Delete', status: 'Succeeded', actor: 'preview-admin', action: 'domain.certificate.delete', resource: `domain:${domain.id}`, detail: domain.mailHostName, remoteAddress: '127.0.0.1', requestId: `req-certificate-${Date.now()}`, relatedId: domain.id, createdAt: new Date().toISOString(), changes: [] })
    return undefined as T
  }

  const domainDkimMatch = url.pathname.match(/^\/domains\/([^/]+)\/dkim$/)
  if (domainDkimMatch && method === 'POST') {
    const domain = domains.find(candidate => candidate.id === domainDkimMatch[1])
    if (!domain) throw new Error('邮件域不存在')
    const request = JSON.parse(String(init.body || '{}')) as { selector?: string }
    const selector = (request.selector ?? '').trim().toLowerCase()
    if (!/^[a-z0-9-]{1,63}$/.test(selector)) throw new Error('DKIM selector 只能包含字母、数字和连字符，长度 1-63。')
    const publicKey = `MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8A${hashPreview(`${domain.id}:${selector}`)}RkCAQEAuG7f2kQm1sV4pLd9xY3bC6nR8aF1eT5oIw==`
    previewDkim[domain.id] = { selector, publicKey }
    previewDnsChecks[domain.id] = createDnsCheck(domain)
    return { publicKey } as T
  }

  const domainDnsValidateMatch = url.pathname.match(/^\/domains\/([^/]+)\/dns\/validate$/)
  if (domainDnsValidateMatch && method === 'POST') {
    const domain = domains.find(candidate => candidate.id === domainDnsValidateMatch[1])
    if (!domain) throw new Error('邮件域不存在')
    const check = previewDnsChecks[domain.id] ?? createDnsCheck(domain)
    if (!check.validation) throw new Error('请先保存 DNS 安全策略后再校验发布。')
    return structuredClone(check.validation) as T
  }

  const domainDnsSettingsMatch = url.pathname.match(/^\/domains\/([^/]+)\/dns$/)
  if (domainDnsSettingsMatch && method === 'GET') {
    const domain = domains.find(candidate => candidate.id === domainDnsSettingsMatch[1])
    if (!domain) throw new Error('邮件域不存在')
    const settings = previewDnsSettings[domain.id] ?? createPreviewDnsSettings(domain)
    return { settings: structuredClone(settings), records: generatePreviewDnsRecords(domain, settings) } as T
  }

  if (domainDnsSettingsMatch && method === 'PUT') {
    const domain = domains.find(candidate => candidate.id === domainDnsSettingsMatch[1])
    if (!domain) throw new Error('邮件域不存在')
    const request = JSON.parse(String(init.body || '{}')) as Record<string, unknown>
    const validationError = validatePreviewDnsSettings(request)
    if (validationError) throw new Error(validationError)
    const normalized: Record<string, unknown> = {
      ...createPreviewDnsSettings(domain),
      ...request,
      spfIpv4: splitPreviewList(request.spfIpv4).join('\n'),
      spfIpv6: splitPreviewList(request.spfIpv6).join('\n'),
      spfIncludes: splitPreviewList(request.spfIncludes).join('\n'),
      spfCustomMechanisms: splitPreviewList(request.spfCustomMechanisms).join('\n'),
      dmarcAggregateReportUris: splitPreviewList(request.dmarcAggregateReportUris).join('\n'),
      dmarcForensicReportUris: splitPreviewList(request.dmarcForensicReportUris).join('\n'),
      mtaStsMxPatterns: splitPreviewList(request.mtaStsMxPatterns).join('\n'),
      tlsReportUris: splitPreviewList(request.tlsReportUris).join('\n'),
      daneTlsaRecords: splitPreviewList(request.daneTlsaRecords).join('\n'),
      dmarcSubdomainPolicy: request.dmarcSubdomainPolicy || null,
      updatedAt: new Date().toISOString(),
    }
    previewDnsSettings[domain.id] = normalized
    previewDnsChecks[domain.id] = createDnsCheck(domain)
    return { settings: structuredClone(normalized), records: generatePreviewDnsRecords(domain, normalized) } as T
  }

  const domainDnsCheckMatch = url.pathname.match(/^\/domains\/([^/]+)\/dns\/check$/)
  if (domainDnsCheckMatch && (method === 'GET' || method === 'POST')) {
    const domain = domains.find(candidate => candidate.id === domainDnsCheckMatch[1])
    if (!domain) throw new Error('邮件域不存在')
    if (method === 'POST') {
      domain.dnsCheckedAt = new Date().toISOString()
      previewDnsChecks[domain.id] = createDnsCheck(domain)
      previewDnsChecks[domain.id]!.checkedAt = domain.dnsCheckedAt
    }
    return structuredClone(previewDnsChecks[domain.id] ?? createDnsCheck(domain)) as T
  }

  const updateDomainQuotaMatch = url.pathname.match(/^\/domains\/([^/]+)\/quota$/)
  if (updateDomainQuotaMatch && method === 'PUT') {
    const domain = domains.find(candidate => candidate.id === updateDomainQuotaMatch[1])
    if (!domain) throw new Error('邮件域不存在')
    const request = JSON.parse(String(init.body)) as { quotaBytes: number }
    if (!Number.isSafeInteger(request.quotaBytes) || request.quotaBytes < 1_048_576) throw new Error('域配额至少为 1 MiB')
    if (request.quotaBytes < domain.usedBytes) throw new Error('域配额不能低于当前已用存储')
    domain.quotaBytes = request.quotaBytes
    addPreviewAudit({ content: '更新域配额', category: 'Update', status: 'Succeeded', actor: 'preview-admin', action: 'domain.quota.update', resource: `domain:${domain.id}`, detail: `quotaBytes=${request.quotaBytes}`, remoteAddress: '127.0.0.1', requestId: `req-domain-${Date.now()}`, relatedId: domain.id, createdAt: new Date().toISOString(), changes: [] })
    return structuredClone(domain) as T
  }

  const updateDomainDetailsMatch = url.pathname.match(/^\/domains\/([^/]+)\/details$/)
  if (updateDomainDetailsMatch && method === 'PUT') {
    const domain = domains.find(candidate => candidate.id === updateDomainDetailsMatch[1])
    if (!domain) throw new Error('邮件域不存在')
    const request = JSON.parse(String(init.body)) as { mailHostName: string; isActive: boolean }
    const host = request.mailHostName.trim().toLowerCase().replace(/\.$/, '')
    if (!/^(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/.test(host)) throw new Error('邮件主机名格式无效')
    domain.mailHostName = host
    domain.isActive = request.isActive
    addPreviewAudit({ content: '更新域配置', category: 'Update', status: 'Succeeded', actor: 'preview-admin', action: 'domain.update', resource: `domain:${domain.id}`, detail: `${host}; active=${request.isActive}`, remoteAddress: '127.0.0.1', requestId: `req-domain-${Date.now()}`, relatedId: domain.id, createdAt: new Date().toISOString(), changes: [] })
    return structuredClone(domain) as T
  }

  const noteDomainAudit = (action: string, category: MockAuditCategory, content: string, resource: string, detail: string, relatedId?: string) =>
    addPreviewAudit({ content, category, status: 'Succeeded', actor: 'preview-admin', action, resource, detail, remoteAddress: '127.0.0.1', requestId: 'req-' + action.replaceAll('.', '-') + '-' + Date.now(), relatedId, createdAt: new Date().toISOString(), changes: [] })

  const findAccount = (id: string) => {
    for (const [domainId, rows] of Object.entries(domainAccounts)) {
      const account = rows.find(candidate => candidate.id === id)
      if (account) return { domainId, account }
    }
    return null
  }

  const findAlias = (id: string) => {
    for (const [domainId, rows] of Object.entries(domainAliases)) {
      const alias = rows.find(candidate => candidate.id === id)
      if (alias) return { domainId, alias }
    }
    return null
  }

  const removeDomainCascade = (domainId: string) => {
    const index = domains.findIndex(candidate => candidate.id === domainId)
    if (index < 0) throw new Error('邮件域不存在')
    const removedName = domains[index]!.name
    const suffix = '@' + removedName
    domains.splice(index, 1)
    delete domainAccounts[domainId]
    delete domainAliases[domainId]
    delete domainRules[domainId]
    delete domainQuarantine[domainId]
    delete domainAuditEntries[domainId]
    delete previewCertificates[domainId]
    delete previewDkim[domainId]
    delete previewDnsSettings[domainId]
    delete previewDnsChecks[domainId]
    deliveryJobs = deliveryJobs.filter(job => !job.sender.endsWith(suffix) && !job.recipient.endsWith(suffix))
  }

  const updateDomainMatch = url.pathname.match(/^\/domains\/([^/]+)$/)
  if (updateDomainMatch && method === 'PATCH') {
    const domain = domains.find(candidate => candidate.id === updateDomainMatch[1])
    if (!domain) throw new Error('邮件域不存在')
    const request = JSON.parse(String(init.body)) as { isActive?: boolean | null; dkimSelector?: string | null; dkimPrivateKeyPath?: string | null }
    if (request.isActive !== undefined && request.isActive !== null) domain.isActive = request.isActive
    if (request.dkimSelector !== undefined && request.dkimSelector !== null) {
      const selector = request.dkimSelector.trim().toLowerCase()
      if (!/^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/.test(selector)) throw new Error('DKIM 选择器格式无效')
      const entry = previewDkim[domain.id] ?? { selector, publicKey: 'MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAuG7f2kQm1sV4pLd9xY3bC6nR8aF1eT5oIw==' }
      entry.selector = selector
      previewDkim[domain.id] = entry
    }
    noteDomainAudit('domain.update', 'Update', '更新域运行参数', 'domain:' + domain.id, 'dkimSelector=' + (previewDkim[domain.id]?.selector ?? '未变更'), domain.id)
    return structuredClone({
      id: domain.id, name: domain.name, mailHostName: domain.mailHostName, smtpHostName: domain.mailHostName,
      outboundIpAddresses: '20.24.67.236', isActive: domain.isActive, quotaBytes: domain.quotaBytes,
      usedBytes: domain.usedBytes, directDeliverSameDomain: true, dkimSelector: previewDkim[domain.id]?.selector,
      dkimPublicKey: previewDkim[domain.id]?.publicKey, createdAt: domain.createdAt,
    }) as T
  }

  if (updateDomainMatch && method === 'DELETE') {
    removeDomainCascade(updateDomainMatch[1]!)
    previewAuditEntries = previewAuditEntries.filter(entry => entry.relatedId !== updateDomainMatch[1])
    noteDomainAudit('domain.delete', 'Delete', '删除邮件域', 'domain:' + updateDomainMatch[1], '域及其邮箱、别名、规则与证书已进入清理队列', updateDomainMatch[1])
    return undefined as T
  }

  if (url.pathname === '/domains/batch/delete' && method === 'POST') {
    const request = JSON.parse(String(init.body)) as { domainIds: string[] }
    if (!request.domainIds.length) throw new Error('请选择需要删除的邮件域')
    request.domainIds.forEach(domainId => removeDomainCascade(domainId))
    noteDomainAudit('domain.batch-delete', 'Delete', '批量删除邮件域', 'domain:batch', 'count=' + request.domainIds.length)
    return undefined as T
  }

  const accountPatchMatch = url.pathname.match(/^\/accounts\/([^/]+)$/)
  if (accountPatchMatch && method === 'PATCH') {
    const found = findAccount(accountPatchMatch[1]!)
    if (!found) throw new Error('邮箱账号不存在')
    const domain = domains.find(candidate => candidate.id === found.domainId)
    const request = JSON.parse(String(init.body)) as { isEnabled?: boolean | null; quotaBytes?: number | null }
    if (request.quotaBytes !== undefined && request.quotaBytes !== null) {
      if (request.quotaBytes <= 0) throw new Error('账号配额必须大于 0')
      if (domain && request.quotaBytes > domain.quotaBytes) throw new Error('账号配额不能超过域配额')
      if (request.quotaBytes < found.account.usedBytes) throw new Error('账号配额不能低于当前已用容量')
      found.account.quotaBytes = request.quotaBytes
    }
    if (request.isEnabled !== undefined && request.isEnabled !== null) found.account.isEnabled = request.isEnabled
    noteDomainAudit('account.update', 'Update', '更新邮箱账号', 'account:' + found.account.id, found.account.address, found.domainId)
    return undefined as T
  }

  const accountPasswordMatch = url.pathname.match(/^\/accounts\/([^/]+)\/password$/)
  if (accountPasswordMatch && method === 'POST') {
    const found = findAccount(accountPasswordMatch[1]!)
    if (!found) throw new Error('邮箱账号不存在')
    const request = JSON.parse(String(init.body)) as { password: string }
    if ((request.password ?? '').length < 12) throw new Error('密码至少需要 12 个字符')
    noteDomainAudit('account.password', 'Update', '重置邮箱密码', 'account:' + found.account.id, found.account.address, found.domainId)
    return undefined as T
  }

  if (accountPatchMatch && method === 'DELETE') {
    const found = findAccount(accountPatchMatch[1]!)
    if (!found) throw new Error('邮箱账号不存在')
    if (found.account.isEnabled) throw new Error('请先停用邮箱账号再删除')
    domainAccounts[found.domainId] = (domainAccounts[found.domainId] ?? []).filter(candidate => candidate.id !== found.account.id)
    const domain = domains.find(candidate => candidate.id === found.domainId)
    if (domain) domain.accountCount = Math.max(0, domain.accountCount - 1)
    ;(domainAliases[found.domainId] ?? []).forEach(alias => { if (alias.targetAccountId === found.account.id) { alias.targetAccountId = null; alias.targetAddress = null; alias.isEnabled = false; alias.canReceive = false; alias.canSend = false } })
    noteDomainAudit('account.soft-delete', 'Delete', '软删除邮箱账号', 'account:' + found.account.id, found.account.address + '；30 天后清理', found.domainId)
    return undefined as T
  }

  const accountBatchMatch = url.pathname.match(/^\/domains\/([^/]+)\/accounts\/batch-(disable|enable|delete)$/)
  if (accountBatchMatch && method === 'POST') {
    const domain = domains.find(candidate => candidate.id === accountBatchMatch[1])
    if (!domain) throw new Error('邮件域不存在')
    const request = JSON.parse(String(init.body)) as { ids: string[] }
    const rows = domainAccounts[domain.id] ?? []
    const targets = rows.filter(row => request.ids.includes(row.id))
    if (targets.length !== new Set(request.ids).size) throw new Error('部分邮箱账号不存在于该域')
    const action = accountBatchMatch[2]!
    if (action === 'delete' && targets.some(row => row.isEnabled)) throw new Error('批量删除前请先停用账号')
    if (action === 'delete') {
      domainAccounts[domain.id] = rows.filter(row => !request.ids.includes(row.id))
      domain.accountCount = Math.max(0, domain.accountCount - targets.length)
      targets.forEach(row => (domainAliases[domain.id] ?? []).forEach(alias => { if (alias.targetAccountId === row.id) { alias.targetAccountId = null; alias.targetAddress = null; alias.isEnabled = false; alias.canReceive = false; alias.canSend = false } }))
    } else {
      targets.forEach(row => { row.isEnabled = action === 'enable' })
    }
    noteDomainAudit('account.batch-' + action, action === 'delete' ? 'Delete' : 'Update', '批量' + (action === 'delete' ? '删除' : action === 'enable' ? '启用' : '停用') + '邮箱账号', 'domain:' + domain.id, 'count=' + targets.length, domain.id)
    return undefined as T
  }

  const rebindAliasMatch = url.pathname.match(/^\/domains\/([^/]+)\/aliases\/([^/]+)$/)
  if (rebindAliasMatch && method === 'PUT') {
    const domain = domains.find(candidate => candidate.id === rebindAliasMatch[1])
    if (!domain) throw new Error('邮件域不存在')
    const alias = (domainAliases[domain.id] ?? []).find(candidate => candidate.id === rebindAliasMatch[2])
    if (!alias) throw new Error('邮箱别名不存在')
    const request = JSON.parse(String(init.body)) as { sourceLocalPart?: string | null; targetAccountId?: string | null; mode?: MockDomainAlias['mode'] | null }
    if (request.sourceLocalPart) {
      const localPart = request.sourceLocalPart.trim().toLowerCase()
      if (!/^[a-z0-9.!#$%&'*+/=?^_`{|}~-]+$/.test(localPart)) throw new Error('别名本地部分格式无效')
      const source = localPart + '@' + domain.name
      if ((domainAliases[domain.id] ?? []).some(candidate => candidate.id !== alias.id && candidate.source === source)) throw new Error('邮箱别名已存在')
      alias.source = source
    }
    if (request.targetAccountId) {
      const account = (domainAccounts[domain.id] ?? []).find(candidate => candidate.id === request.targetAccountId)
      if (!account) throw new Error('目标邮箱账号不存在')
      if (!account.isEnabled) throw new Error('目标邮箱账号已停用')
      alias.targetAccountId = account.id
      alias.targetAddress = account.address
    }
    if (request.mode) {
      if (request.mode === 'ReceiveOnly' && !alias.targetAccountId) throw new Error('仅收信别名仍需绑定目标邮箱')
      alias.mode = request.mode
      alias.isEnabled = true
      alias.canReceive = true
      alias.canSend = request.mode === 'SendReceive'
    }
    noteDomainAudit('alias.update', 'Update', '更新邮箱别名', 'alias:' + alias.id, alias.source + ' -> ' + (alias.targetAddress ?? '未绑定'), domain.id)
    return structuredClone(alias) as T
  }

  const aliasDeleteMatch = url.pathname.match(/^\/aliases\/([^/]+)$/)
  if (aliasDeleteMatch && method === 'DELETE') {
    const found = findAlias(aliasDeleteMatch[1]!)
    if (!found) throw new Error('邮箱别名不存在')
    domainAliases[found.domainId] = (domainAliases[found.domainId] ?? []).filter(candidate => candidate.id !== found.alias.id)
    const account = (domainAccounts[found.domainId] ?? []).find(candidate => candidate.id === found.alias.targetAccountId)
    if (account) account.aliasCount = Math.max(0, account.aliasCount - 1)
    noteDomainAudit('alias.delete', 'Delete', '删除邮箱别名', 'alias:' + found.alias.id, found.alias.source, found.domainId)
    return undefined as T
  }

  const aliasBatchModeMatch = url.pathname.match(/^\/domains\/([^/]+)\/aliases\/batch-mode$/)
  if (aliasBatchModeMatch && method === 'POST') {
    const domain = domains.find(candidate => candidate.id === aliasBatchModeMatch[1])
    if (!domain) throw new Error('邮件域不存在')
    const request = JSON.parse(String(init.body)) as { ids: string[]; mode: MockDomainAlias['mode'] }
    const rows = domainAliases[domain.id] ?? []
    const targets = rows.filter(row => request.ids.includes(row.id))
    if (targets.length !== new Set(request.ids).size) throw new Error('部分别名不存在于该域')
    if (targets.some(row => !row.targetAccountId || !(domainAccounts[domain.id] ?? []).some(account => account.id === row.targetAccountId && account.isEnabled))) throw new Error('启用的别名需要绑定已启用的目标邮箱')
    targets.forEach(row => { row.mode = request.mode; row.isEnabled = true; row.canReceive = true; row.canSend = request.mode === 'SendReceive' })
    noteDomainAudit('alias.batch-mode', 'Update', '批量调整别名模式', 'domain:' + domain.id, 'count=' + targets.length + ';mode=' + request.mode, domain.id)
    return undefined as T
  }

  const aliasBatchDeleteMatch = url.pathname.match(/^\/domains\/([^/]+)\/aliases\/batch-delete$/)
  if (aliasBatchDeleteMatch && method === 'POST') {
    const domain = domains.find(candidate => candidate.id === aliasBatchDeleteMatch[1])
    if (!domain) throw new Error('邮件域不存在')
    const request = JSON.parse(String(init.body)) as { ids: string[] }
    domainAliases[domain.id] = (domainAliases[domain.id] ?? []).filter(row => !request.ids.includes(row.id))
    noteDomainAudit('alias.batch-delete', 'Delete', '批量删除邮箱别名', 'domain:' + domain.id, 'count=' + request.ids.length, domain.id)
    return undefined as T
  }

  if (url.pathname.endsWith('/rule-folders') && method === 'GET') {
    const domain = domains.find(candidate => candidate.id === url.pathname.split('/')[2])
    if (!domain) throw new Error('邮件域不存在')
    const total = (domainAccounts[domain.id] ?? []).filter(account => account.isEnabled).length
    const folders = ['INBOX', 'Sent', 'Drafts', 'Trash', 'Junk', 'Archives']
    return folders.map(name => ({ name, availableAccounts: Math.min(total, Math.max(0, name === 'INBOX' ? total : total - 1)), totalAccounts: total })) as T
  }

  const domainRuleCreateMatch = url.pathname.match(/^\/domains\/([^/]+)\/rules$/)
  if (domainRuleCreateMatch && method === 'POST') {
    const domain = domains.find(candidate => candidate.id === domainRuleCreateMatch[1])
    if (!domain) throw new Error('邮件域不存在')
    const request = JSON.parse(String(init.body)) as {
      name: string; direction: MockDomainRule['direction']; matchMode: MockDomainRule['matchMode']
      targetMailboxName: string; isEnabled: boolean
      conditions: Array<{ field: string; operator: string; values: string[] }>
      actions?: Array<{ kind: string; values: string[] }> | null
    }
    const name = (request.name ?? '').trim()
    if (!name) throw new Error('规则名称不能为空')
    if (!(request.conditions ?? []).length) throw new Error('至少需要一个匹配条件')
    if (!request.targetMailboxName) throw new Error('缺少目标文件夹或 Sent 落盘位置')
    if (request.direction !== 'Incoming' && request.direction !== 'Outgoing') throw new Error('规则方向无效')
    // null（旧客户端未提交 actions）补默认移动动作；显式空数组视为错误，避免静默增加动作。
    const submittedActions = request.actions == null
      ? [{ kind: 'MoveToFolder', values: [request.targetMailboxName] }]
      : request.actions
    if (!submittedActions.length) throw new Error('请至少添加一个执行动作')
    const rows = domainRules[domain.id] ??= []
    const sameDirection = rows.filter(rule => rule.direction === request.direction)
    const id = globalThis.crypto.randomUUID()
    const rule: MockDomainRule = {
      id, domainId: domain.id, name, direction: request.direction, matchMode: request.matchMode ?? 'All',
      targetMailboxName: request.targetMailboxName, isEnabled: request.isEnabled !== false,
      priority: sameDirection.reduce((max, candidate) => Math.max(max, candidate.priority), -1) + 1,
      conditions: request.conditions.map((condition, index) => ({ id: globalThis.crypto.randomUUID(), field: condition.field, operator: condition.operator, values: condition.values ?? [], position: index })),
      actions: submittedActions.map((action, index) => ({ id: globalThis.crypto.randomUUID(), kind: action.kind, values: action.values ?? [], position: index })),
    }
    rows.push(rule)
    noteDomainAudit('rule.create', 'Create', '创建收发信规则', 'rule:' + id, name, domain.id)
    return structuredClone(rule) as T
  }

  if (url.pathname.endsWith('/rules/order') && method === 'PUT') {
    const domain = domains.find(candidate => candidate.id === url.pathname.split('/')[2])
    if (!domain) throw new Error('邮件域不存在')
    const request = JSON.parse(String(init.body)) as { direction: MockDomainRule['direction']; ruleIds: string[] }
    const rows = (domainRules[domain.id] ?? []).filter(rule => rule.direction === request.direction)
    if (rows.length !== request.ruleIds.length || rows.some(rule => !request.ruleIds.includes(rule.id))) throw new Error('排序列表必须包含该方向的全部规则')
    request.ruleIds.forEach((ruleId, index) => { const rule = rows.find(candidate => candidate.id === ruleId); if (rule) rule.priority = index })
    noteDomainAudit('rule.reorder', 'Update', '调整收发信规则顺序', 'domain:' + domain.id, 'direction=' + request.direction + ';count=' + rows.length, domain.id)
    return undefined as T
  }

  const domainRuleItemMatch = url.pathname.match(/^\/domains\/([^/]+)\/rules\/([^/]+)$/)
  if (domainRuleItemMatch && (method === 'PUT' || method === 'DELETE')) {
    const domain = domains.find(candidate => candidate.id === domainRuleItemMatch[1])
    if (!domain) throw new Error('邮件域不存在')
    const rows = domainRules[domain.id] ?? []
    const rule = rows.find(candidate => candidate.id === domainRuleItemMatch[2])
    if (!rule) throw new Error('收发信规则不存在')
    if (method === 'DELETE') {
      domainRules[domain.id] = rows.filter(candidate => candidate.id !== rule.id)
      noteDomainAudit('rule.delete', 'Delete', '删除收发信规则', 'rule:' + rule.id, rule.name, domain.id)
      return undefined as T
    }
    const request = JSON.parse(String(init.body)) as { name?: string; direction?: MockDomainRule['direction']; matchMode?: MockDomainRule['matchMode']; targetMailboxName?: string; isEnabled?: boolean; conditions?: Array<{ field: string; operator: string; values: string[] }>; actions?: Array<{ kind: string; values: string[] }> | null }
    if (!request.name?.trim() || !request.direction || !request.matchMode || !request.targetMailboxName || !request.conditions?.length || typeof request.isEnabled !== 'boolean') {
      throw new Error('更新规则必须提交完整规则，不能只提交启用状态')
    }
    if (request.actions != null && !request.actions.length) throw new Error('请至少添加一个执行动作')
    rule.direction = request.direction
    if (request.name !== undefined) rule.name = request.name.trim()
    if (request.matchMode) rule.matchMode = request.matchMode
    if (request.targetMailboxName) rule.targetMailboxName = request.targetMailboxName
    if (request.isEnabled !== undefined) rule.isEnabled = request.isEnabled
    if (request.conditions) rule.conditions = request.conditions.map((condition, index) => ({ id: globalThis.crypto.randomUUID(), field: condition.field, operator: condition.operator, values: condition.values ?? [], position: index }))
    if (request.actions) rule.actions = request.actions.map((action, index) => ({ id: globalThis.crypto.randomUUID(), kind: action.kind, values: action.values ?? [], position: index }))
    noteDomainAudit('rule.update', 'Update', '更新收发信规则', 'rule:' + rule.id, rule.name, domain.id)
    return structuredClone(rule) as T
  }

  const domainAuditMatch = url.pathname.match(/^\/domains\/([^/]+)\/audit$/)
  if (domainAuditMatch && method === 'GET') {
    if (!domains.some(candidate => candidate.id === domainAuditMatch[1])) throw new Error('邮件域不存在')
    return structuredClone(domainAuditEntries[domainAuditMatch[1]!] ?? []) as T
  }

  const quarantineListMatch = url.pathname.match(/^\/domains\/([^/]+)\/quarantine$/)
  if (quarantineListMatch && method === 'GET') {
    if (!domains.some(candidate => candidate.id === quarantineListMatch[1])) throw new Error('邮件域不存在')
    const search = url.searchParams.get('search')?.trim().toLowerCase()
    const status = url.searchParams.get('status')
    const rows = (domainQuarantine[quarantineListMatch[1]!] ?? []).filter(entry =>
      (!search || entry.sender.toLowerCase().includes(search) || entry.recipients.join(' ').toLowerCase().includes(search) || entry.reason.toLowerCase().includes(search))
      && (!status || entry.status === status))
    // 契约与后端 PagedQuarantineResult 对齐：分页字段名是 total，不是 totalCount。
    const pageNumber = Number(url.searchParams.get('page') || 1)
    const pageSize = Number(url.searchParams.get('pageSize') || 20)
    return {
      items: rows.slice((pageNumber - 1) * pageSize, pageNumber * pageSize),
      page: pageNumber,
      pageSize,
      total: rows.length,
    } as T
  }

  const quarantineRawMatch = url.pathname.match(/^\/domains\/([^/]+)\/quarantine\/([^/]+)\/raw$/)
  if (quarantineRawMatch && method === 'GET') {
    const entry = (domainQuarantine[quarantineRawMatch[1]!] ?? []).find(candidate => candidate.id === quarantineRawMatch[2])
    if (!entry) throw new Error('隔离邮件不存在')
    return {
      record: structuredClone(entry),
      fileName: 'quarantine-' + entry.id.replaceAll('-', '') + '.eml',
      content: [
        'From: ' + entry.sender,
        'To: ' + entry.recipients.join(', '),
        'Date: ' + new Date(entry.quarantinedAt).toUTCString(),
        'Message-ID: ' + (entry.internetMessageId ?? '<' + entry.messageId + '@mailserver.preview>'),
        'Subject: MailServer preview quarantined message',
        'MIME-Version: 1.0',
        'Content-Type: text/plain; charset=utf-8',
        '',
        '隔离原因：' + entry.reason,
        'This is a redacted preview message body. Production content is read from the quarantine store.',
      ].join('\r\n'),
    } as T
  }

  const quarantineActionMatch = url.pathname.match(/^\/domains\/([^/]+)\/quarantine\/([^/]+)(\/release)?$/)
  if (quarantineActionMatch && (method === 'POST' || method === 'DELETE')) {
    const domainId = quarantineActionMatch[1]!
    if (!domains.some(candidate => candidate.id === domainId)) throw new Error('邮件域不存在')
    const rows = domainQuarantine[domainId] ?? []
    const entry = rows.find(candidate => candidate.id === quarantineActionMatch[2])
    if (!entry) throw new Error('隔离邮件不存在')
    if (entry.status !== 'PendingReview') throw new Error('该隔离邮件已审核，无法重复操作')
    const request = init.body ? JSON.parse(String(init.body)) as { note?: string | null } : {}
    const releasing = method === 'POST' && Boolean(quarantineActionMatch[3])
    entry.status = releasing ? 'Released' : 'Deleted'
    entry.reviewedBy = 'preview-admin'
    entry.reviewedAt = new Date().toISOString()
    entry.reviewNote = request.note?.trim() || (releasing ? '人工审核后放行' : '人工审核后删除')
    noteDomainAudit('quarantine.' + (releasing ? 'release' : 'delete'), releasing ? 'Update' : 'Delete', releasing ? '放行隔离邮件' : '删除隔离邮件', 'quarantine:' + entry.id, entry.sender + ' -> ' + entry.recipients.join(', '), domainId)
    return releasing ? undefined as T : undefined as T
  }

  const domainQueueMatch = url.pathname.match(/^\/domains\/([^/]+)\/queue$/)
  if (domainQueueMatch && method === 'GET') {
    const domain = domains.find(candidate => candidate.id === domainQueueMatch[1])
    if (!domain) throw new Error('邮件域不存在')
    const search = url.searchParams.get('search')?.toLowerCase()
    const status = url.searchParams.get('status')
    const scope = url.searchParams.get('scope')
    const suffix = '@' + domain.name
    const filtered = deliveryJobs.filter(job => (job.sender.endsWith(suffix) || job.recipient.endsWith(suffix)))
      .filter(job => (!search || job.sender.toLowerCase().includes(search) || job.recipient.toLowerCase().includes(search) || job.lastError?.toLowerCase().includes(search))
        && (!status || job.status === status)
        && (scope !== 'Active' || job.status !== 'Delivered')
        && (scope !== 'History' || job.status === 'Delivered')
        && (scope !== 'Failed' || job.status === 'Failed'))
      .sort((left, right) => new Date(right.updatedAt).getTime() - new Date(left.updatedAt).getTime())
    return page(filtered, Number(url.searchParams.get('page') || 1), Number(url.searchParams.get('pageSize') || 20)) as T
  }

  const domainQueueRawMatch = url.pathname.match(/^\/domains\/([^/]+)\/queue\/([^/]+)\/raw$/)
  if (domainQueueRawMatch && method === 'GET') {
    const job = deliveryJobs.find(item => item.id === domainQueueRawMatch[2])
    if (!job) throw new Error('投递任务不存在')
    return {
      deliveryJobId: job.id,
      fileName: 'delivery-' + job.id.replaceAll('-', '') + '.eml',
      content: [
        'From: ' + job.sender,
        'To: ' + job.recipient,
        'Date: ' + new Date(job.createdAt).toUTCString(),
        'Message-ID: <' + job.id + '@mailserver.preview>',
        'Subject: MailServer preview delivery message',
        'MIME-Version: 1.0',
        'Content-Type: text/plain; charset=utf-8',
        '',
        'This is a redacted preview message body. Production content is read from the configured message store.',
      ].join('\r\n'),
    } as T
  }

  const domainQueueActionMatch = url.pathname.match(/^\/domains\/([^/]+)\/queue\/([^/]+)(\/retry)?$/)
  if (domainQueueActionMatch && (method === 'POST' || method === 'DELETE')) {
    const job = deliveryJobs.find(item => item.id === domainQueueActionMatch[2])
    if (!job) throw new Error('投递任务不存在')
    if (method === 'POST') {
      job.status = 'Pending'; job.nextAttemptAt = new Date().toISOString(); job.updatedAt = new Date().toISOString(); job.lastError = undefined
      return undefined as T
    }
    deliveryJobs = deliveryJobs.filter(item => item.id !== job.id)
    return undefined as T
  }

  if (url.pathname === '/admins/me/password' && method === 'POST') {
    const request = JSON.parse(String(init.body)) as { currentPassword: string; newPassword: string }
    if (request.currentPassword !== 'preview-password') throw new Error('当前密码不正确')
    if ((request.newPassword ?? '').length < 12) throw new Error('新密码至少需要 12 个字符')
    return undefined as T
  }
  if (url.pathname === '/queues/summary') {
    return {
      pending: deliveryJobs.filter(job => job.status === 'Pending').length,
      processing: deliveryJobs.filter(job => job.status === 'Processing').length,
      deferred: deliveryJobs.filter(job => job.status === 'Deferred').length,
      failed: deliveryJobs.filter(job => job.status === 'Failed').length,
      deliveredLast24Hours: deliveryJobs.filter(job => job.status === 'Delivered').length,
      failedLast24Hours: deliveryJobs.filter(job => job.status === 'Failed').length,
      averageAttemptMillisecondsLast24Hours: (() => {
        const attempts = deliveryJobs.flatMap(job => deliveryAttempts[job.id] ?? [])
        return attempts.length ? attempts.reduce((sum, attempt) => sum + attempt.durationMilliseconds, 0) / attempts.length : 0
      })(),
    } as T
  }

  if (url.pathname === '/queues') {
    const search = url.searchParams.get('search')?.toLowerCase()
    const status = url.searchParams.get('status')
    const scope = url.searchParams.get('scope')
    const filtered = deliveryJobs.filter(job =>
      (!search || job.sender.toLowerCase().includes(search) || job.recipient.toLowerCase().includes(search) || job.lastError?.toLowerCase().includes(search))
      && (!status || job.status === status)
      && (scope !== 'Active' || job.status !== 'Delivered')
      && (scope !== 'History' || job.status === 'Delivered')
      && (scope !== 'Failed' || job.status === 'Failed'))
      .sort((left, right) => new Date(right.updatedAt).getTime() - new Date(left.updatedAt).getTime())
    return page(filtered, Number(url.searchParams.get('page') || 1), Number(url.searchParams.get('pageSize') || 20)) as T
  }

  const attemptMatch = url.pathname.match(/^\/queues\/([^/]+)\/attempts$/)
  if (attemptMatch) {
    const job = deliveryJobs.find(item => item.id === attemptMatch[1])
    if (!job) throw new Error('投递任务不存在')
    return structuredClone(deliveryAttempts[job.id] ?? [])
      .sort((left, right) => right.attemptNumber - left.attemptNumber)
      .map(attempt => ({ ...attempt, sessionSummary: attempt.sessionSummary?.replace(/<[^<>\s@]+@([^<>\s]+)>/g, '<***@$1>') })) as T
  }

  const rawMatch = url.pathname.match(/^\/queues\/([^/]+)\/raw$/)
  if (rawMatch && method === 'GET') {
    const job = deliveryJobs.find(item => item.id === rawMatch[1])
    if (!job) throw new Error('投递任务不存在')
    return {
      deliveryJobId: job.id,
      fileName: `delivery-${job.id.replaceAll('-', '')}.eml`,
      content: [
        `From: ${job.sender}`,
        `To: ${job.recipient}`,
        `Date: ${new Date(job.createdAt).toUTCString()}`,
        `Message-ID: <${job.id}@mailserver.preview>`,
        'Subject: MailServer preview delivery message',
        'MIME-Version: 1.0',
        'Content-Type: text/plain; charset=utf-8',
        '',
        'This is a redacted preview message body. Production content is read from the configured message store.',
      ].join('\r\n'),
    } as T
  }

  const retryMatch = url.pathname.match(/^\/queues\/([^/]+)\/retry$/)
  if (retryMatch && method === 'POST') {
    const job = deliveryJobs.find(item => item.id === retryMatch[1])
    if (!job) throw new Error('投递任务不存在')
    job.status = 'Pending'; job.nextAttemptAt = new Date().toISOString(); job.updatedAt = new Date().toISOString(); job.lastError = undefined
    addPreviewAudit({ content: '立即重试投递任务', category: 'BackgroundTask', status: 'Succeeded', actor: 'preview-admin', action: 'queue.retry', resource: `delivery:${job.id}`, detail: `${job.sender} -> ${job.recipient}`, remoteAddress: '127.0.0.1', requestId: `req-queue-${Date.now()}`, relatedId: job.id, createdAt: new Date().toISOString(), changes: [] })
    return undefined as T
  }

  const deleteMatch = url.pathname.match(/^\/queues\/([^/]+)$/)
  if (deleteMatch && method === 'DELETE') {
    const job = deliveryJobs.find(item => item.id === deleteMatch[1])
    if (!job) throw new Error('投递任务不存在')
    deliveryJobs = deliveryJobs.filter(job => job.id !== deleteMatch[1])
    addPreviewAudit({ content: '删除投递任务', category: 'Delete', status: 'Succeeded', actor: 'preview-admin', action: 'queue.delete', resource: `delivery:${job.id}`, detail: `${job.sender} -> ${job.recipient}`, remoteAddress: '127.0.0.1', requestId: `req-queue-${Date.now()}`, relatedId: job.id, createdAt: new Date().toISOString(), changes: [] })
    return undefined as T
  }

  if (url.pathname === '/diagnostics/dns' && method === 'GET') {
    const domain = (url.searchParams.get('domain') || '').trim().toLowerCase().replace(/\.$/, '')
    const selector = (url.searchParams.get('selector') || '').trim()
    if (!/^(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/.test(domain)) throw new Error('请输入有效的收件域名')
    const nullMx = domain === 'blocked.invalid'
    const mxHosts = nullMx ? ['0 .'] : domain === 'gmail.com'
      ? ['5 gmail-smtp-in.l.google.com', '10 alt1.gmail-smtp-in.l.google.com']
      : [`10 mx1.${domain}`, `20 mx2.${domain}`]
    const hosts = nullMx ? [] : mxHosts.map(item => item.split(' ')[1]!)
    return {
      domain,
      mx: mxHosts,
      spf: nullMx ? [] : ['v=spf1 mx -all'],
      dmarc: nullMx ? [] : ['v=DMARC1; p=quarantine; rua=mailto:dmarc@example.test'],
      dkim: selector && !nullMx ? [`v=DKIM1; k=rsa; p=MIIBIjANBgkqh…`] : [],
      mtaSts: domain === 'gmail.com' ? ['v=STSv1; id=20260918T000000'] : [],
      tlsRpt: nullMx ? [] : ['v=TLSRPTv1; rua=mailto:tls-rpt@example.test'],
      tlsa: domain.endsWith('.secure.example') ? [`${hosts[0]} 3 1 1 2A4C6E8F…`] : [],
      mxConnectivity: hosts.flatMap((host, index) => [
        { host, address: `203.0.113.${25 + index}`, addressFamily: 'IPv4', port: 25, success: true, error: null },
        { host, address: `2001:db8:${index + 1}::25`, addressFamily: 'IPv6', port: 25, success: index === 0, error: index === 0 ? null : '连接超时' },
      ]),
      warnings: nullMx
        ? ['Recipient domain publishes Null MX and does not accept email.']
        : domain === 'gmail.com'
          ? []
          : [`MX host ${hosts[1]} has IPv6 address 2001:db8:2::25, but SMTP port 25 is not reachable over IPv6.`, 'No MTA-STS policy found; opportunistic STARTTLS will be used.'],
    } as T
  }

  if (url.pathname === '/delivery/tls-reports' && method === 'GET') {
    const search = url.searchParams.get('search')?.trim().toLowerCase()
    const status = url.searchParams.get('status') as MockTlsReportStatus | null
    const filtered = tlsReportDeliveries.filter(report =>
      (!search || report.policyDomain.toLowerCase().includes(search) || report.reportUri.toLowerCase().includes(search) || report.lastError?.toLowerCase().includes(search))
      && (!status || report.status === status))
      .sort((left, right) => new Date(right.periodEnd).getTime() - new Date(left.periodEnd).getTime())
      .map(report => ({
        id: report.id,
        policyDomain: report.policyDomain,
        reportUri: report.reportUri,
        periodStart: report.periodStart,
        periodEnd: report.periodEnd,
        status: report.status,
        attempts: report.attempts,
        nextAttemptAt: report.nextAttemptAt,
        completedAt: report.completedAt,
        lastError: report.lastError,
      }))
    return page(filtered, Number(url.searchParams.get('page') || 1), Number(url.searchParams.get('pageSize') || 20)) as T
  }

  const tlsReportDetailMatch = url.pathname.match(/^\/delivery\/tls-reports\/([^/]+)$/)
  if (tlsReportDetailMatch && method === 'GET') {
    const report = tlsReportDeliveries.find(item => item.id === tlsReportDetailMatch[1])
    if (!report) throw new Error('TLS 报告不存在')
    return structuredClone(report) as T
  }

  const tlsReportRetryMatch = url.pathname.match(/^\/delivery\/tls-reports\/([^/]+)\/retry$/)
  if (tlsReportRetryMatch && method === 'POST') {
    const report = tlsReportDeliveries.find(item => item.id === tlsReportRetryMatch[1])
    if (!report) throw new Error('TLS 报告不存在')
    if (report.completedAt) throw new Error('已完成的 TLS 报告不能重试')
    report.status = 'Pending'; report.attempts = 0; report.nextAttemptAt = new Date().toISOString(); report.lastError = undefined
    addPreviewAudit({ content: '重试 TLS 报告投递', category: 'BackgroundTask', status: 'Succeeded', actor: 'preview-admin', action: 'tls-report.retry', resource: `tls-report:${report.id}`, detail: report.policyDomain, remoteAddress: '127.0.0.1', requestId: `req-tls-report-${Date.now()}`, relatedId: report.id, createdAt: new Date().toISOString(), changes: [] })
    return undefined as T
  }

  throw new Error(`预览数据尚未覆盖接口：${method} ${url.pathname}`)
}

export async function mockApiDownload(path: string, init: RequestInit = {}) {
  const url = new globalThis.URL(path, 'http://preview.local')
  const method = (init.method || 'GET').toUpperCase()
  if (url.pathname !== '/audit/export' || method !== 'POST') {
    throw new Error(`预览下载尚未覆盖接口：${method} ${url.pathname}`)
  }

  const input = JSON.parse(String(init.body)) as { ids?: string[] }
  const ids = input.ids ?? []
  if (ids.length > 1000) throw new Error('单次最多导出 1000 条审计记录')
  const records = ids
    .map(id => previewAuditEntries.find(entry => entry.id === id))
    .filter((entry): entry is MockAuditEntry => Boolean(entry))
  return {
    blob: new Blob([JSON.stringify(records, null, 2)], { type: 'application/json; charset=utf-8' }),
    fileName: `audit-preview-${new Date().toISOString().slice(0, 10).replaceAll('-', '')}.json`,
  }
}
