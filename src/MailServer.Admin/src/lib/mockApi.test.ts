import { describe, expect, it } from 'vitest'
import { enablePreviewMode, mockApi, mockApiDownload } from './mockApi'

interface PagedResult<T> {
  items: T[]
  totalCount: number
}

interface AdminUser {
  id: string
  userName: string
  isEnabled: boolean
  isPrimary: boolean
}

interface GrantInput {
  permissionCode: string
  resourceType: string
  resourceKey: string
}

interface ApiKeyRecord {
  id: string
  name: string
  prefix: string
  revokedAt?: string
}

interface ScheduledTask {
  id: string
  type: string
  name: string
  isEnabled: boolean
  intervalMinutes: number
  timeoutSeconds: number
  batchSize: number
  status: string
}

interface TaskExecution {
  id: string
  scheduledTaskId: string
  status: string
  processedCount: number
}

interface AuditRecord {
  id: string
  number: string
  content: string
  category: string
  status: string
  action?: string
  changes?: Array<{ field: string; oldValue?: string; newValue?: string }>
  rawJson?: string
}

interface PluginRecord {
  id: string
  displayName: string
  enabled: boolean
  isHostActive: boolean
  loadError?: string
  effectiveOrder: number
}

interface PluginConfiguration {
  plugin: PluginRecord
  definitions: Array<{ key: string; type: string; minimum?: number; maximum?: number }>
  values: Array<{ key: string; value: unknown; hasSecret: boolean }>
}

interface MessageTemplateSettings {
  senderDisplayName: string
  permanentFailureSubject: string
  permanentFailureBody: string
  retryExhaustedSubject: string
  retryExhaustedBody: string
}

interface RecycleBinItem {
  queueId: string
  resourceType: 'Domain' | 'Account' | 'Mailbox' | 'MailboxMessage' | 'ContentFile'
  resourceId: string | null
  displayName: string
  estimatedBytes: number
  status: 'Pending' | 'Failed'
}

describe('administrator preview API', () => {
  it('supports a complete administrator lifecycle without losing grants', async () => {
    const userName = `preview-test-${crypto.randomUUID().slice(0, 8)}`
    const grants: GrantInput[] = [
      { permissionCode: 'domain.read', resourceType: 'domain', resourceKey: '9e8fef40-335f-4d7a-a6c5-7379bb9aa401' },
    ]

    const created = await mockApi<AdminUser>('/admins', {
      method: 'POST',
      body: JSON.stringify({ userName, password: 'preview-safe-2026', isEnabled: true, grants }),
    })
    expect(created).toMatchObject({ userName, isEnabled: true, isPrimary: false })

    const filtered = await mockApi<PagedResult<AdminUser>>(`/admins?search=${userName}&page=1&pageSize=20`)
    expect(filtered.totalCount).toBe(1)
    expect(filtered.items[0]?.id).toBe(created.id)
    await expect(mockApi<GrantInput[]>(`/admins/${created.id}/grants`)).resolves.toEqual(grants)

    const updatedGrants: GrantInput[] = [
      ...grants,
      { permissionCode: 'audit.read', resourceType: '', resourceKey: '' },
    ]
    await mockApi(`/admins/${created.id}/grants`, {
      method: 'PUT',
      body: JSON.stringify({ grants: updatedGrants }),
    })
    await expect(mockApi<GrantInput[]>(`/admins/${created.id}/grants`)).resolves.toEqual(updatedGrants)

    await mockApi(`/admins/${created.id}`, {
      method: 'PATCH',
      body: JSON.stringify({ userName, isEnabled: false, password: null }),
    })
    const disabled = await mockApi<PagedResult<AdminUser>>(`/admins?status=disabled&search=${userName}&page=1&pageSize=20`)
    expect(disabled.items[0]?.isEnabled).toBe(false)

    await mockApi(`/admins/${created.id}`, { method: 'DELETE' })
    const deleted = await mockApi<PagedResult<AdminUser>>(`/admins?search=${userName}&page=1&pageSize=20`)
    expect(deleted.totalCount).toBe(0)
  })

  it('enforces password and primary-administrator protections', async () => {
    await expect(mockApi('/admins', {
      method: 'POST',
      body: JSON.stringify({
        userName: `weak-${crypto.randomUUID().slice(0, 8)}`,
        password: 'short',
        isEnabled: true,
        grants: [{ permissionCode: 'dashboard.read', resourceType: '', resourceKey: '' }],
      }),
    })).rejects.toThrow('密码至少需要 12 个字符')

    await expect(mockApi('/admins/d25d8438-3d40-4696-83da-101d142b2aa1', { method: 'DELETE' }))
      .rejects.toThrow('主管理员不能删除')
    await expect(mockApi('/admins/d25d8438-3d40-4696-83da-101d142b2aa1/grants', {
      method: 'PUT',
      body: JSON.stringify({ grants: [{ permissionCode: 'dashboard.read', resourceType: '', resourceKey: '' }] }),
    })).rejects.toThrow('主管理员始终拥有全部权限')
  })
})

describe('API Key preview API', () => {
  it('creates, rotates, updates grants, and deletes a key', async () => {
    const name = `connector-${crypto.randomUUID().slice(0, 8)}`
    const initialGrants: GrantInput[] = [{ permissionCode: 'dashboard.read', resourceType: '', resourceKey: '' }]

    const created = await mockApi<{ id: string; apiKey: string }>('/api-keys', {
      method: 'POST',
      body: JSON.stringify({ name, grants: initialGrants, expiresAt: null }),
    })
    expect(created.apiKey).toMatch(/^msk_preview_/)

    const listed = await mockApi<PagedResult<ApiKeyRecord>>('/api-keys')
    const record = listed.items.find(item => item.id === created.id)
    expect(record).toMatchObject({ name })

    const updatedGrants: GrantInput[] = [
      { permissionCode: 'domain.read', resourceType: 'domain', resourceKey: '*' },
      { permissionCode: 'audit.read', resourceType: '', resourceKey: '' },
    ]
    await mockApi(`/api-keys/${created.id}/grants`, {
      method: 'PUT',
      body: JSON.stringify({ grants: updatedGrants }),
    })
    await expect(mockApi<GrantInput[]>(`/api-keys/${created.id}/grants`)).resolves.toEqual(updatedGrants)

    const rotated = await mockApi<{ apiKey: string }>(`/api-keys/${created.id}/refresh`, { method: 'POST' })
    expect(rotated.apiKey).toMatch(/^msk_preview_/)
    expect(rotated.apiKey).not.toBe(created.apiKey)

    await mockApi(`/api-keys/${created.id}`, { method: 'DELETE' })
    const afterDelete = await mockApi<PagedResult<ApiKeyRecord>>('/api-keys')
    expect(afterDelete.items.some(item => item.id === created.id)).toBe(false)
  })

  it('rejects empty grants, past expiration, and rotation of revoked credentials', async () => {
    await expect(mockApi('/api-keys', {
      method: 'POST',
      body: JSON.stringify({ name: `empty-${crypto.randomUUID().slice(0, 8)}`, grants: [], expiresAt: null }),
    })).rejects.toThrow('至少需要授予一项权限')

    await expect(mockApi('/api-keys', {
      method: 'POST',
      body: JSON.stringify({
        name: `expired-${crypto.randomUUID().slice(0, 8)}`,
        grants: [{ permissionCode: 'dashboard.read', resourceType: '', resourceKey: '' }],
        expiresAt: '2020-01-01T00:00:00Z',
      }),
    })).rejects.toThrow('过期时间必须晚于当前时间')

    await expect(mockApi('/api-keys/de90c87a-bc83-437a-86c4-61c1c28ccb03/refresh', { method: 'POST' }))
      .rejects.toThrow('已吊销的 API Key 不能轮换')
  })
})

describe('scheduled-task preview API', () => {
  it('clamps task settings, records the change, and creates a manual execution', async () => {
    const tasks = await mockApi<ScheduledTask[]>('/tasks')
    const task = tasks.find(item => item.type === 'cache.cleanup')
    expect(task).toBeDefined()

    await mockApi(`/tasks/${task!.id}`, {
      method: 'PUT',
      body: JSON.stringify({ enabled: true, intervalMinutes: 0, timeoutSeconds: 1, batchSize: 9999 }),
    })
    const updated = (await mockApi<ScheduledTask[]>('/tasks')).find(item => item.id === task!.id)
    expect(updated).toMatchObject({ isEnabled: true, intervalMinutes: 1, timeoutSeconds: 10, batchSize: 1000, status: 'Idle' })

    const run = await mockApi<{ executionId: string }>(`/tasks/${task!.id}/run`, { method: 'POST' })
    const executions = await mockApi<TaskExecution[]>('/tasks/executions?take=50')
    expect(executions[0]).toMatchObject({ id: run.executionId, scheduledTaskId: task!.id, status: 'Succeeded' })

    const taskAudits = await mockApi<PagedResult<AuditRecord>>('/audit?search=task.update&page=1&pageSize=20')
    expect(taskAudits.totalCount).toBeGreaterThan(0)
  })

  it('rejects a duplicate run for a task that is already running', async () => {
    const tasks = await mockApi<ScheduledTask[]>('/tasks')
    const runningTask = tasks.find(item => item.status === 'Running')
    expect(runningTask).toBeDefined()
    await expect(mockApi(`/tasks/${runningTask!.id}/run`, { method: 'POST' }))
      .rejects.toThrow('任务正在运行，不能重复触发')
  })
})

describe('audit preview API', () => {
  it('filters by the real backend enum values and returns complete detail', async () => {
    const warnings = await mockApi<PagedResult<AuditRecord>>('/audit?status=Warning&page=1&pageSize=20')
    expect(warnings.totalCount).toBeGreaterThanOrEqual(2)
    expect(warnings.items.every(item => item.status === 'Warning')).toBe(true)

    const plugins = await mockApi<PagedResult<AuditRecord>>('/audit?category=Plugin&page=1&pageSize=20')
    expect(plugins.totalCount).toBeGreaterThanOrEqual(2)
    expect(plugins.items.every(item => item.category === 'Plugin')).toBe(true)

    const domainUpdates = await mockApi<PagedResult<AuditRecord>>('/audit?search=domain.update&page=1&pageSize=20')
    const detail = await mockApi<AuditRecord>(`/audit/${domainUpdates.items[0]!.id}`)
    expect(detail.action).toBe('domain.update')
    expect(detail.changes).toHaveLength(2)
    expect(detail.rawJson).toContain('"password": "***"')
  })

  it('no longer exposes legacy cluster or node identifiers after the cluster module was removed', async () => {
    const records = await mockApi<PagedResult<AuditRecord>>('/audit?page=1&pageSize=100')
    const serialized = JSON.stringify(records)
    expect(serialized).not.toContain(['preview', 'primary'].join('-'))
    expect(serialized).not.toMatch(/nodeId|nodeName/i)

    const tasks = await mockApi<ScheduledTask[]>('/tasks')
    expect(JSON.stringify(tasks)).not.toMatch(/nodeId|nodeName/i)

    const executions = await mockApi<TaskExecution[]>('/tasks/executions?take=50')
    expect(JSON.stringify(executions)).not.toMatch(/nodeId|nodeName/i)
  })

  it('exports only selected records and enforces the 1000-record limit', async () => {
    const records = await mockApi<PagedResult<AuditRecord>>('/audit?page=1&pageSize=2')
    const selected = records.items[0]!
    const result = await mockApiDownload('/audit/export', {
      method: 'POST',
      body: JSON.stringify({ ids: [selected.id] }),
    })
    expect(result.fileName).toMatch(/^audit-preview-\d{8}\.json$/)
    const exported = JSON.parse(await result.blob.text()) as AuditRecord[]
    expect(exported).toHaveLength(1)
    expect(exported[0]?.id).toBe(selected.id)

    await expect(mockApiDownload('/audit/export', {
      method: 'POST',
      body: JSON.stringify({ ids: Array.from({ length: 1001 }, (_, index) => String(index)) }),
    })).rejects.toThrow('单次最多导出 1000 条审计记录')
  })
})

describe('plugin preview API', () => {
  it('models enabled, disabled, healthy, and failed plugins with real configuration shapes', async () => {
    const plugins = await mockApi<PluginRecord[]>('/plugins')
    expect(plugins).toHaveLength(4)
    expect(plugins.some(plugin => !plugin.enabled)).toBe(true)
    expect(plugins.some(plugin => plugin.isHostActive)).toBe(true)
    expect(plugins.some(plugin => plugin.loadError)).toBe(true)

    const configuration = await mockApi<PluginConfiguration>('/plugins/mailserver-mime-validation/configuration')
    expect(configuration.definitions.some(field => field.key === 'maximumMimeParts' && field.minimum === 1 && field.maximum === 10000)).toBe(true)
    expect(configuration.values.find(setting => setting.key === 'maximumMimeParts')?.value).toBe(1000)
  })

  it('validates settings and supports secret replacement and explicit clearing', async () => {
    await expect(mockApi('/plugins/mailserver-mime-validation/configuration', {
      method: 'PUT',
      body: JSON.stringify({ enabled: true, values: { maximumMimeParts: 0 }, clearedSecrets: [] }),
    })).rejects.toThrow('最大 MIME 部件数超出允许范围')

    const path = '/plugins/sample-large-message-policy/configuration'
    await mockApi(path, {
      method: 'PUT',
      body: JSON.stringify({ enabled: true, values: { exampleIdentity: 'operator@northwind.cn', exampleBearerToken: 'replacement-secret' }, clearedSecrets: [] }),
    })
    const replaced = await mockApi<PluginConfiguration>(path)
    expect(replaced.plugin.enabled).toBe(true)
    expect(replaced.values.find(setting => setting.key === 'exampleBearerToken')).toMatchObject({ value: null, hasSecret: true })

    await mockApi(path, {
      method: 'PUT',
      body: JSON.stringify({ enabled: true, values: { exampleIdentity: 'operator@northwind.cn' }, clearedSecrets: ['exampleBearerToken'] }),
    })
    const cleared = await mockApi<PluginConfiguration>(path)
    expect(cleared.values.find(setting => setting.key === 'exampleBearerToken')?.hasSecret).toBe(false)
  })

  it('persists a complete unique execution order and records it in audit', async () => {
    const plugins = await mockApi<PluginRecord[]>('/plugins')
    const reversedIds = plugins.map(plugin => plugin.id).reverse()
    const reordered = await mockApi<PluginRecord[]>('/plugins/order', {
      method: 'PUT',
      body: JSON.stringify({ pluginIds: reversedIds }),
    })
    expect(reordered.map(plugin => plugin.id)).toEqual(reversedIds)
    expect(reordered.map(plugin => plugin.effectiveOrder)).toEqual([10, 20, 30, 40])

    await expect(mockApi('/plugins/order', {
      method: 'PUT',
      body: JSON.stringify({ pluginIds: [reversedIds[0], reversedIds[0]] }),
    })).rejects.toThrow('插件顺序包含未知、重复或缺失的插件')

    const audits = await mockApi<PagedResult<AuditRecord>>('/audit?search=plugin.order.update&page=1&pageSize=20')
    expect(audits.totalCount).toBeGreaterThan(0)
  })
})

describe('message-template preview API', () => {
  it('enforces backend length boundaries, normalizes values, and records an audit event', async () => {
    await expect(mockApi('/settings/message-templates', {
      method: 'PUT',
      body: JSON.stringify({
        senderDisplayName: '',
        permanentFailureSubject: '永久失败',
        permanentFailureBody: '<p>失败</p>',
        retryExhaustedSubject: '重试耗尽',
        retryExhaustedBody: '<p>重试耗尽</p>',
      }),
    })).rejects.toThrow('发件人显示名称必须为 1 至 120 个字符')

    const input: MessageTemplateSettings = {
      senderDisplayName: '  MailServer 投递中心  ',
      permanentFailureSubject: '  投递失败  ',
      permanentFailureBody: '  <p>{recipient}：{reason}</p>  ',
      retryExhaustedSubject: '  重试耗尽  ',
      retryExhaustedBody: '  <p>{queueId} 已尝试 {attempts} 次</p>  ',
    }
    const saved = await mockApi<MessageTemplateSettings>('/settings/message-templates', {
      method: 'PUT',
      body: JSON.stringify(input),
    })
    expect(saved).toEqual({
      senderDisplayName: 'MailServer 投递中心',
      permanentFailureSubject: '投递失败',
      permanentFailureBody: '<p>{recipient}：{reason}</p>',
      retryExhaustedSubject: '重试耗尽',
      retryExhaustedBody: '<p>{queueId} 已尝试 {attempts} 次</p>',
    })
    await expect(mockApi<MessageTemplateSettings>('/settings/message-templates')).resolves.toEqual(saved)

    const audits = await mockApi<PagedResult<AuditRecord>>('/audit?search=settings.message-templates.update&page=1&pageSize=20')
    expect(audits.totalCount).toBeGreaterThan(0)
  })
})

describe('recycle-bin preview API', () => {
  it('models real resource shapes, enforces restore constraints, and records actions', async () => {
    const initial = await mockApi<RecycleBinItem[]>('/recycle-bin')
    expect(initial.length).toBeGreaterThanOrEqual(5)
    expect(initial.some(item => item.status === 'Failed')).toBe(true)
    expect(initial.some(item => item.resourceType === 'ContentFile' && item.resourceId === null)).toBe(true)
    expect(initial.every(item => item.estimatedBytes >= 0)).toBe(true)

    const contentFile = initial.find(item => item.resourceType === 'ContentFile')!
    await expect(mockApi(`/recycle-bin/${contentFile.queueId}/restore`, { method: 'POST' }))
      .rejects.toThrow('该资源不支持恢复')

    const restorable = initial.find(item => item.resourceType === 'Account')!
    await mockApi(`/recycle-bin/${restorable.queueId}/restore`, { method: 'POST' })
    const afterRestore = await mockApi<RecycleBinItem[]>('/recycle-bin')
    expect(afterRestore.some(item => item.queueId === restorable.queueId)).toBe(false)

    await mockApi(`/recycle-bin/${contentFile.queueId}`, { method: 'DELETE' })
    const afterPurge = await mockApi<RecycleBinItem[]>('/recycle-bin')
    expect(afterPurge.some(item => item.queueId === contentFile.queueId)).toBe(false)

    const restoreAudits = await mockApi<PagedResult<AuditRecord>>('/audit?search=recycle.restore&page=1&pageSize=20')
    const purgeAudits = await mockApi<PagedResult<AuditRecord>>('/audit?search=recycle.purge&page=1&pageSize=20')
    expect(restoreAudits.totalCount).toBeGreaterThan(0)
    expect(purgeAudits.totalCount).toBeGreaterThan(0)
  })
})

describe('domain-detail preview API', () => {
  const domainId = '9e8fef40-335f-4d7a-a6c5-7379bb9aa401'

  it('serves the complete domain workspace and stateful Direct MX checks', async () => {
    const detail = await mockApi<{ domain: { name: string; usedBytes: number }; certificate: { subject: string } | null; connections: unknown[] }>(`/domains/${domainId}`)
    expect(detail.domain.name).toBe('northwind.cn')
    expect(detail.certificate?.subject).toContain('mail.northwind.cn')
    expect(detail.connections).toHaveLength(2)

    const health = await mockApi<{ accountCount: number; aliasCount: number }>(`/domains/${domainId}/health`)
    expect(health.accountCount).toBeGreaterThan(0)
    expect(health.aliasCount).toBeGreaterThan(0)

    const checked = await mockApi<{ status: string; validation: { items: unknown[]; warnings: string[] } }>(`/domains/${domainId}/dns/check`, { method: 'POST' })
    expect(checked.status).toBe('Healthy')
    expect(checked.validation.items).toHaveLength(5)
    expect(checked.validation.warnings).toHaveLength(1)
  })

  it('paginates domain accounts and aliases with full coverage, and returns the complete account list for alias targets', async () => {
    const pageSize = 20
    const accountsPage1 = await mockApi<PagedResult<{ id: string; address: string }>>(`/domains/${domainId}/accounts/search?page=1&pageSize=${pageSize}`)
    expect(accountsPage1.totalCount).toBeGreaterThanOrEqual(45)
    expect(accountsPage1.items).toHaveLength(pageSize)

    const seenAccounts = new Set<string>()
    const totalPages = Math.ceil(accountsPage1.totalCount / pageSize)
    for (let page = 1; page <= totalPages; page++) {
      const result = await mockApi<PagedResult<{ id: string; address: string }>>(`/domains/${domainId}/accounts/search?page=${page}&pageSize=${pageSize}`)
      expect(result.totalCount).toBe(accountsPage1.totalCount)
      for (const item of result.items) {
        expect(seenAccounts.has(item.address)).toBe(false)
        seenAccounts.add(item.address)
      }
    }
    expect(seenAccounts.size).toBe(accountsPage1.totalCount)

    const aliasesPage1 = await mockApi<PagedResult<{ id: string }>>(`/domains/${domainId}/aliases/search?page=1&pageSize=${pageSize}`)
    expect(aliasesPage1.totalCount).toBeGreaterThanOrEqual(45)
    expect(aliasesPage1.items).toHaveLength(pageSize)

    const seenAliases = new Set<string>()
    const aliasTotalPages = Math.ceil(aliasesPage1.totalCount / pageSize)
    for (let page = 1; page <= aliasTotalPages; page++) {
      const result = await mockApi<PagedResult<{ id: string }>>(`/domains/${domainId}/aliases/search?page=${page}&pageSize=${pageSize}`)
      for (const item of result.items) {
        expect(seenAliases.has(item.id)).toBe(false)
        seenAliases.add(item.id)
      }
    }
    expect(seenAliases.size).toBe(aliasesPage1.totalCount)

    // 别名目标选择器使用非分页接口，必须是整个域的全量启用账号，而不是当前页。
    const allAccounts = await mockApi<Array<{ id: string; address: string; isEnabled: boolean }>>(`/domains/${domainId}/accounts`)
    expect(allAccounts.length).toBe(accountsPage1.totalCount)
    expect(allAccounts).toEqual(expect.arrayContaining(accountsPage1.items))
    expect(allAccounts.some(account => !account.isEnabled)).toBe(true)
    expect(allAccounts.slice(0, allAccounts.findIndex(account => !account.isEnabled)).every(account => account.isEnabled)).toBe(true)
  })

  it('creates searchable accounts and aliases and enforces quota safety', async () => {
    const suffix = crypto.randomUUID().slice(0, 8)
    await expect(mockApi(`/domains/${domainId}/accounts`, {
      method: 'POST',
      body: JSON.stringify({ localPart: `short-${suffix}`, password: 'weak', quotaBytes: 1_073_741_824 }),
    })).rejects.toThrow('密码至少需要 12 个字符')

    const account = await mockApi<{ id: string; address: string }>(`/domains/${domainId}/accounts`, {
      method: 'POST',
      body: JSON.stringify({ localPart: `qa-${suffix}`, password: 'safe-preview-2026', quotaBytes: 2_147_483_648 }),
    })
    const accounts = await mockApi<PagedResult<{ id: string; address: string }>>(`/domains/${domainId}/accounts/search?query=qa-${suffix}&page=1&pageSize=20`)
    expect(accounts.items).toEqual([account])

    const alias = await mockApi<{ source: string; targetAddress: string }>(`/domains/${domainId}/aliases`, {
      method: 'POST',
      body: JSON.stringify({ sourceLocalPart: `alias-${suffix}`, targetAccountId: account.id, mode: 'ReceiveOnly' }),
    })
    expect(alias.targetAddress).toBe(account.address)
    const aliases = await mockApi<PagedResult<{ source: string }>>(`/domains/${domainId}/aliases/search?query=alias-${suffix}&page=1&pageSize=20`)
    expect(aliases.items[0]?.source).toBe(`alias-${suffix}@northwind.cn`)

    const detail = await mockApi<{ domain: { usedBytes: number } }>(`/domains/${domainId}`)
    await expect(mockApi(`/domains/${domainId}/quota`, {
      method: 'PUT',
      body: JSON.stringify({ quotaBytes: detail.domain.usedBytes - 1 }),
    })).rejects.toThrow('域配额不能低于当前已用存储')
    await expect(mockApi(`/domains/${domainId}/quota`, {
      method: 'PUT',
      body: JSON.stringify({ quotaBytes: detail.domain.usedBytes + 10_737_418_240 }),
    })).resolves.toBeDefined()
  })
  it('validates pasted PEM and rejects unusable certificates before enabling', async () => {
    const pasted = (certificate: string, privateKey: string, password = '') => {
      const form = new FormData()
      form.set('certificate', new File([certificate], 'pasted-chain.pem'))
      form.set('privateKey', new File([privateKey], 'pasted-key.pem'))
      if (password) form.set('password', password)
      return form
    }
    const key = '-----BEGIN PRIVATE KEY-----\nabc\n-----END PRIVATE KEY-----'

    await expect(mockApi(`/domains/${domainId}/certificate/inspect-upload`, { method: 'POST', body: pasted('not-a-certificate', key) }))
      .rejects.toThrow('证书不是有效的 PEM 格式')

    const wrongHost = await mockApi<{ coversMailHostName: boolean }>(`/domains/${domainId}/certificate/inspect-upload`, {
      method: 'POST',
      body: pasted('-----BEGIN CERTIFICATE-----\nCN=other.example\n-----END CERTIFICATE-----', key),
    })
    expect(wrongHost.coversMailHostName).toBe(false)
    await expect(mockApi(`/domains/${domainId}/certificate/upload`, {
      method: 'POST',
      body: pasted('-----BEGIN CERTIFICATE-----\nCN=other.example\n-----END CERTIFICATE-----', key),
    })).rejects.toThrow('证书 SAN 未覆盖 mail.northwind.cn')

    const clientAuth = await mockApi<{ supportsServerAuthentication: boolean }>(`/domains/${domainId}/certificate/inspect-upload`, {
      method: 'POST',
      body: pasted('-----BEGIN CERTIFICATE-----\nDNS:mail.northwind.cn clientAuth\n-----END CERTIFICATE-----', key),
    })
    expect(clientAuth.supportsServerAuthentication).toBe(false)

    const mismatchedKeyPem = '-----BEGIN PRIVATE KEY-----\nmismatched-key\n-----END PRIVATE KEY-----'
    const mismatchedKeyCert = '-----BEGIN CERTIFICATE-----\nDNS:mail.northwind.cn\n-----END CERTIFICATE-----'
    const mismatched = await mockApi<{ hasMatchingPrivateKey: boolean }>(`/domains/${domainId}/certificate/inspect-upload`, {
      method: 'POST',
      body: pasted(mismatchedKeyCert, mismatchedKeyPem),
    })
    expect(mismatched.hasMatchingPrivateKey).toBe(false)
    await expect(mockApi(`/domains/${domainId}/certificate/upload`, {
      method: 'POST',
      body: pasted(mismatchedKeyCert, mismatchedKeyPem),
    })).rejects.toThrow('证书与私钥不匹配')
  })

  it('replaces, binds and deletes certificates in preview mode', async () => {
    const targetDomainId = '9e8fef40-335f-4d7a-a6c5-7379bb9aa403'
    const inspect = await mockApi<{ subject: string; coversMailHostName: boolean }>(`/domains/${targetDomainId}/certificate/inspect-path`, {
      method: 'POST',
      body: JSON.stringify({ certificatePath: 'D:\\certs\\fullchain.pem', privateKeyPath: 'D:\\certs\\private.key' }),
    })
    expect(inspect.coversMailHostName).toBe(true)
    await expect(mockApi(`/domains/${targetDomainId}/certificate/inspect-path`, {
      method: 'POST',
      body: JSON.stringify({ certificatePath: 'relative/fullchain.pem', privateKeyPath: 'D:\\certs\\private.key' }),
    })).rejects.toThrow('证书链必须是绝对路径')

    const bound = await mockApi<{ source: string; certificatePath: string }>(`/domains/${targetDomainId}/certificate/path`, {
      method: 'PUT',
      body: JSON.stringify({ certificatePath: 'D:\\certs\\fullchain.pem', privateKeyPath: 'D:\\certs\\private.key' }),
    })
    expect(bound.source).toBe('ExternalPath')

    const detail = await mockApi<{ certificate: { source: string; thumbprint: string } | null }>(`/domains/${targetDomainId}`)
    expect(detail.certificate?.source).toBe('ExternalPath')
    const health = await mockApi<{ hasCertificate: boolean }>(`/domains/${targetDomainId}/health`)
    expect(health.hasCertificate).toBe(true)

    await expect(mockApi(`/domains/${targetDomainId}/certificate`, { method: 'DELETE' })).resolves.toBeUndefined()
    const cleared = await mockApi<{ certificate: unknown; }>(`/domains/${targetDomainId}`)
    expect(cleared.certificate).toBeNull()
    const clearedHealth = await mockApi<{ hasCertificate: boolean }>(`/domains/${targetDomainId}/health`)
    expect(clearedHealth.hasCertificate).toBe(false)
  })
  it('exposes quarantine paging as total to match the backend PagedQuarantineResult', async () => {
    await enablePreviewMode()
    const domainId = '9e8fef40-335f-4d7a-a6c5-7379bb9aa401'
    const result = await mockApi<{ items: unknown[]; page: number; pageSize: number; total: number }>(
      `/domains/${domainId}/quarantine?page=1&pageSize=10`)
    // 域详情页依赖 total 字段渲染分页；缺失会显示「共 条记录 / 第 1 / NaN 页」。
    expect(typeof result.total).toBe('number')
    expect(result.total).toBeGreaterThan(0)
    expect(result).not.toHaveProperty('totalCount')
  })

  it('manages accounts, aliases, rules, quarantine, audit and domain queue through the preview API', async () => {
    const domainName = 'northwind.cn'
    const suffix = crypto.randomUUID().slice(0, 8)
    const domainId = '9e8fef40-335f-4d7a-a6c5-7379bb9aa401'

    // --- accounts: create -> patch -> password -> batch toggle -> soft delete ---
    const account = await mockApi<{ id: string; address: string }>(`/domains/${domainId}/accounts`, {
      method: 'POST',
      body: JSON.stringify({ localPart: `ops-${suffix}`, password: 'safe-preview-2026', quotaBytes: 4_294_967_296 }),
    })
    await expect(mockApi(`/accounts/${account.id}`, {
      method: 'PATCH',
      body: JSON.stringify({ quotaBytes: 1099511627777 }),
    })).rejects.toThrow('账号配额不能超过域配额')
    const inUse = (await mockApi<PagedResult<{ id: string; usedBytes: number }>>(`/domains/${domainId}/accounts/search?page=1&pageSize=50`)).items.find(item => item.usedBytes > 0)
    if (inUse) {
      await expect(mockApi(`/accounts/${inUse.id}`, {
        method: 'PATCH',
        body: JSON.stringify({ quotaBytes: 1 }),
      })).rejects.toThrow('账号配额不能低于当前已用容量')
    }
    await mockApi(`/accounts/${account.id}`, { method: 'PATCH', body: JSON.stringify({ isEnabled: false, quotaBytes: 2_147_483_648 }) })
    await mockApi(`/accounts/${account.id}/password`, { method: 'POST', body: JSON.stringify({ password: 'another-safe-password' }) })
    await expect(mockApi(`/accounts/${account.id}/password`, { method: 'POST', body: JSON.stringify({ password: 'short' }) })).rejects.toThrow('密码至少需要 12 个字符')
    const accounts = await mockApi<PagedResult<{ id: string; isEnabled: boolean; quotaBytes: number; usedBytes: number }>>(`/domains/${domainId}/accounts/search?page=1&pageSize=50`)
    const disabled = accounts.items.find(item => item.id === account.id)
    expect(disabled?.isEnabled).toBe(false)
    await mockApi(`/domains/${domainId}/accounts/batch-enable`, { method: 'POST', body: JSON.stringify({ ids: [account.id] }) })
    await expect(mockApi(`/accounts/${account.id}`, { method: 'DELETE' })).rejects.toThrow('请先停用邮箱账号再删除')
    await mockApi(`/domains/${domainId}/accounts/batch-disable`, { method: 'POST', body: JSON.stringify({ ids: [account.id] }) })
    await mockApi(`/accounts/${account.id}`, { method: 'DELETE' })

    // --- aliases: create -> rebind -> batch mode -> delete ---
    const target = accounts.items.find(item => item.isEnabled)
    const alias = await mockApi<{ id: string; source: string; mode: string }>(`/domains/${domainId}/aliases`, {
      method: 'POST',
      body: JSON.stringify({ sourceLocalPart: `qa-${suffix}`, targetAccountId: target!.id, mode: 'SendReceive' }),
    })
    expect(alias.source).toBe(`qa-${suffix}@${domainName}`)
    const rebound = await mockApi<{ mode: string; canSend: boolean }>(`/domains/${domainId}/aliases/${alias.id}`, {
      method: 'PUT',
      body: JSON.stringify({ targetAccountId: target!.id, mode: 'ReceiveOnly' }),
    })
    expect(rebound.mode).toBe('ReceiveOnly')
    expect(rebound.canSend).toBe(false)
    await mockApi(`/domains/${domainId}/aliases/batch-mode`, { method: 'POST', body: JSON.stringify({ ids: [alias.id], mode: 'SendReceive' }) })
    await mockApi(`/domains/${domainId}/aliases/batch-delete`, { method: 'POST', body: JSON.stringify({ ids: [alias.id] }) })
    const aliases = await mockApi<PagedResult<{ id: string }>>(`/domains/${domainId}/aliases/search?page=1&pageSize=50`)
    expect(aliases.items.some(item => item.id === alias.id)).toBe(false)

    // --- rules: create -> reorder -> update -> delete ---
    const incoming = await mockApi<Array<{ id: string; priority: number; direction: string }>>(`/domains/${domainId}/rules?direction=Incoming`)
    const created = await mockApi<{ id: string; priority: number; direction: string }>(`/domains/${domainId}/rules`, {
      method: 'POST',
      body: JSON.stringify({
        name: `QA 规则 ${suffix}`, direction: 'Incoming', matchMode: 'Any', targetMailboxName: 'Junk', isEnabled: true,
        conditions: [{ field: 'Subject', operator: 'ContainsAny', values: ['qa-preview'] }],
        actions: [{ kind: 'MoveToFolder', values: ['Junk'] }],
      }),
    })
    expect(created.priority).toBeGreaterThan(Math.max(...incoming.map(rule => rule.priority)))
    // 显式提交空 actions 必须被拒绝；省略 actions 的旧客户端仍由后端补默认移动动作。
    await expect(mockApi(`/domains/${domainId}/rules`, {
      method: 'POST',
      body: JSON.stringify({
        name: `QA 空动作 ${suffix}`, direction: 'Incoming', matchMode: 'All', targetMailboxName: 'Junk', isEnabled: true,
        conditions: [{ field: 'Subject', operator: 'ContainsAny', values: ['qa-preview'] }],
        actions: [],
      }),
    })).rejects.toThrow('至少添加一个执行动作')
    await expect(mockApi(`/domains/${domainId}/rules`, {
      method: 'POST',
      body: JSON.stringify({ name: '', direction: 'Incoming', matchMode: 'All', targetMailboxName: 'INBOX', isEnabled: true, conditions: [] }),
    })).rejects.toThrow('规则名称不能为空')
    const reordered = [created.id, ...incoming.map(rule => rule.id)]
    await mockApi(`/domains/${domainId}/rules/order`, { method: 'PUT', body: JSON.stringify({ direction: 'Incoming', ruleIds: reordered }) })
    await expect(mockApi(`/domains/${domainId}/rules/order`, { method: 'PUT', body: JSON.stringify({ direction: 'Incoming', ruleIds: [created.id] }) })).rejects.toThrow('排序列表必须包含该方向的全部规则')
    await expect(mockApi(`/domains/${domainId}/rules/${created.id}`, { method: 'PUT', body: JSON.stringify({ name: `QA 规则 ${suffix}`, isEnabled: false }) })).rejects.toThrow('完整规则')
    await mockApi(`/domains/${domainId}/rules/${created.id}`, { method: 'PUT', body: JSON.stringify({ ...created, name: `QA 规则 ${suffix}`, isEnabled: false }) })
    const afterUpdate = await mockApi<Array<{ id: string; isEnabled: boolean; priority: number }>>(`/domains/${domainId}/rules?direction=Incoming`)
    const updated = afterUpdate.find(rule => rule.id === created.id)
    expect(updated?.isEnabled).toBe(false)
    // 排序用 0 起始的序号，与后端 ReorderAsync 一致。
    expect(updated?.priority).toBe(0)

    const folders = await mockApi<Array<{ name: string; availableAccounts: number; totalAccounts: number }>>(`/domains/${domainId}/rule-folders`)
    expect(folders.some(folder => folder.name === 'INBOX')).toBe(true)

    // --- quarantine: list -> raw -> release -> delete ---
    const quarantine = await mockApi<PagedResult<{ id: string; status: string }>>(`/domains/${domainId}/quarantine?status=PendingReview&page=1&pageSize=10`)
    expect(quarantine.items.length).toBeGreaterThan(0)
    const pending = quarantine.items[0]!
    const raw = await mockApi<{ record: { id: string }; fileName: string; content: string; truncated: boolean }>(`/domains/${domainId}/quarantine/${pending.id}/raw`)
    expect(raw.record.id).toBe(pending.id)
    expect(raw.content).toContain('Message-ID')
    expect(raw.truncated).toBe(false)
    const rawDownload = await mockApiDownload(`/domains/${domainId}/quarantine/${pending.id}/raw/download`)
    expect(rawDownload.fileName).toMatch(/^quarantine-[a-f0-9]+\.eml$/)
    expect(await rawDownload.blob.text()).toContain('Message-ID')
    await mockApi(`/domains/${domainId}/quarantine/${pending.id}/release`, { method: 'POST', body: JSON.stringify({ note: '预览放行' }) })
    await expect(mockApi(`/domains/${domainId}/quarantine/${pending.id}/release`, { method: 'POST', body: JSON.stringify({ note: '重复' }) })).rejects.toThrow('该隔离邮件已审核')
    const released = await mockApi<PagedResult<{ id: string; status: string }>>(`/domains/${domainId}/quarantine?status=Released&page=1&pageSize=10`)
    expect(released.items.some(item => item.id === pending.id)).toBe(true)
    const second = (await mockApi<PagedResult<{ id: string }>>(`/domains/${domainId}/quarantine?status=PendingReview&page=1&pageSize=10`)).items[0]!
    await mockApi(`/domains/${domainId}/quarantine/${second.id}`, { method: 'DELETE', body: JSON.stringify({ note: '预览删除' }) })

    // --- audit + domain queue ---
    const audit = await mockApi<Array<{ id: string; action: string }>>(`/domains/${domainId}/audit`)
    expect(audit.length).toBeGreaterThan(0)
    const queue = await mockApi<PagedResult<{ id: string; sender: string; recipient: string; status: string; attempts: number }>>(`/domains/${domainId}/queue?scope=Any&page=1&pageSize=20`)
    expect(queue.items.every(job => job.sender.endsWith(`@${domainName}`) || job.recipient.endsWith(`@${domainName}`))).toBe(true)
    if (queue.items.length) {
      const jobId = queue.items[0]!.id
      const rawMessage = await mockApi<{ deliveryJobId: string }>(`/domains/${domainId}/queue/${jobId}/raw`)
      expect(rawMessage.deliveryJobId).toBe(jobId)
      if (queue.items[0]!.status === 'Failed' || queue.items[0]!.status === 'Deferred')
        await mockApi(`/domains/${domainId}/queue/${jobId}/retry`, { method: 'POST', body: JSON.stringify({ expectedAttempts: queue.items[0]!.attempts }) })
      else
        await expect(mockApi(`/domains/${domainId}/queue/${jobId}/retry`, { method: 'POST', body: JSON.stringify({ expectedAttempts: queue.items[0]!.attempts }) })).rejects.toThrow('任务状态已变化')
    }

    // --- 发信规则：转发 / 拒绝发信分支 ---
    const outgoing = await mockApi<Array<{ id: string; direction: string; actions: Array<{ kind: string }> }>>(`/domains/${domainId}/rules?direction=Outgoing`)
    expect(outgoing.some(rule => rule.actions.some(action => action.kind === 'Reject'))).toBe(true)
    const rejected = await mockApi<{ id: string; direction: string }>(`/domains/${domainId}/rules`, {
      method: 'POST',
      body: JSON.stringify({
        name: `QA 拒信 ${suffix}`, direction: 'Outgoing', matchMode: 'All', targetMailboxName: 'Sent', isEnabled: true,
        conditions: [{ field: 'Subject', operator: 'ContainsAny', values: ['*机密*'] }],
        actions: [{ kind: 'Reject', values: ['禁止外发'] }],
      }),
    })
    expect(rejected.direction).toBe('Outgoing')
    await mockApi(`/domains/${domainId}/rules/${rejected.id}`, { method: 'DELETE' })
    await expect(mockApi(`/domains/${domainId}/rules`, {
      method: 'POST',
      body: JSON.stringify({ name: 'bad direction', direction: 'Sideways', matchMode: 'All', targetMailboxName: 'Sent', isEnabled: true, conditions: [{ field: 'Subject', operator: 'ContainsAny', values: ['x'] }] }),
    })).rejects.toThrow('规则方向无效')

    // --- cleanup rule + admin password branches ---
    await mockApi(`/domains/${domainId}/rules/${created.id}`, { method: 'DELETE' })
    await mockApi('/admins/me/password', { method: 'POST', body: JSON.stringify({ currentPassword: 'preview-password', newPassword: 'new-preview-password' }) })
    await expect(mockApi('/admins/me/password', { method: 'POST', body: JSON.stringify({ currentPassword: 'wrong', newPassword: 'new-preview-password' }) })).rejects.toThrow('当前密码不正确')
  })

  it('exposes domain update, delete and batch delete without disturbing other preview data', async () => {
    const created = await mockApi<{ id: string; name: string }>('/domains', { method: 'POST', body: JSON.stringify({ name: 'qa-delete.example' }) })
    const patched = await mockApi<{ isActive: boolean; dkimSelector: string }>(`/domains/${created.id}`, {
      method: 'PATCH',
      body: JSON.stringify({ isActive: false, dkimSelector: 'qa2026' }),
    })
    expect(patched.isActive).toBe(false)
    expect(patched.dkimSelector).toBe('qa2026')
    await expect(mockApi(`/domains/${created.id}`, { method: 'PATCH', body: JSON.stringify({ dkimSelector: 'Bad Selector!' }) })).rejects.toThrow('DKIM 选择器格式无效')

    const second = await mockApi<{ id: string }>('/domains', { method: 'POST', body: JSON.stringify({ name: 'qa-batch.example' }) })
    await mockApi('/domains/batch/delete', { method: 'POST', body: JSON.stringify({ domainIds: [created.id, second.id] }) })
    const search = await mockApi<PagedResult<{ id: string }>>('/domains/search?search=qa-&page=1&pageSize=50')
    expect(search.items.some(item => item.id === created.id)).toBe(false)
    expect(search.items.some(item => item.id === second.id)).toBe(false)
  })

  it('creates a normalized empty domain without fabricated health data', async () => {
    const suffix = crypto.randomUUID().slice(0, 8)
    const name = `qa-health-${suffix}.example.test`
    const created = await mockApi<{ id: string; name: string; mailHostName: string }>('/domains', {
      method: 'POST',
      body: JSON.stringify({ name: `  ${name.toUpperCase()}. `, dkimSelector: 'MAIL2026' }),
    })

    expect(created.name).toBe(name)
    expect(created.mailHostName).toBe(`mail.${name}`)

    const detail = await mockApi<{ domain: { dkimSelector?: string; dkimPublicKey?: string } }>(`/domains/${created.id}`)
    expect(detail.domain.dkimSelector).toBe('mail2026')
    expect(detail.domain.dkimPublicKey).toBeUndefined()

    const health = await mockApi<{
      accountCount: number
      enabledAccountCount: number
      aliasCount: number
      pendingDeliveries: number
      failedDeliveries: number
      hasDkim: boolean
      lastAuditAt: string | null
      pendingQuarantineCount: number
    }>(`/domains/${created.id}/health`)
    expect(health).toMatchObject({
      accountCount: 0,
      enabledAccountCount: 0,
      aliasCount: 0,
      pendingDeliveries: 0,
      failedDeliveries: 0,
      hasDkim: false,
      pendingQuarantineCount: 0,
    })
    expect(health.lastAuditAt).toBeTruthy()

    const audit = await mockApi<Array<{ action: string }>>(`/domains/${created.id}/audit`)
    expect(audit.map(entry => entry.action)).toContain('domain.create')
    await expect(mockApi('/domains', { method: 'POST', body: JSON.stringify({ name }) })).rejects.toThrow('邮件域已存在')
    await expect(mockApi('/domains', { method: 'POST', body: JSON.stringify({ name: `bad-${suffix}.example.test`, dkimSelector: '-invalid' }) })).rejects.toThrow('不能以连字符开头或结尾')

    const dnsCheck = await mockApi<{
      status: string
      checkedAt: string | null
      validation: { items: Array<{ status: string; actual: string[] }>; warnings: string[] } | null
    }>(`/domains/${created.id}/dns/check`, { method: 'POST' })
    expect(dnsCheck.status).toBe('Unhealthy')
    expect(dnsCheck.checkedAt).toBeTruthy()
    expect(dnsCheck.validation?.items.every(item => item.status === 'missing' && item.actual.length === 0)).toBe(true)
    expect(dnsCheck.validation?.warnings.join(' ')).toContain('尚未为这个新域模拟发布 DNS 记录')

    await mockApi(`/domains/${created.id}/dkim`, { method: 'POST', body: JSON.stringify({ selector: 'mail2026' }) })
    const generatedHealth = await mockApi<{ hasDkim: boolean }>(`/domains/${created.id}/health`)
    expect(generatedHealth.hasDkim).toBe(true)

    await mockApi('/domains/batch/delete', { method: 'POST', body: JSON.stringify({ domainIds: [created.id] }) })
  })

  it('provides paged and filtered firewall results for the production-shaped UI', async () => {
    const prefix = `qa-firewall-${Date.now()}`
    for (let index = 0; index < 25; index++) {
      await mockApi('/firewall/rules', {
        method: 'POST',
        body: JSON.stringify({ cidr: `10.77.${index}.1`, note: `${prefix}-${index}` }),
      })
    }
    const first = await mockApi<PagedResult<{ note: string }>>(`/firewall/rules/search?query=${prefix}&page=1&pageSize=20`)
    const second = await mockApi<PagedResult<{ note: string }>>(`/firewall/rules/search?query=${prefix}&page=2&pageSize=20`)
    expect(first.totalCount).toBe(25)
    expect(first.items).toHaveLength(20)
    expect(second.items).toHaveLength(5)
    expect([...first.items, ...second.items].every(rule => rule.note.startsWith(prefix))).toBe(true)
  })
})
