import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, nextTick } from 'vue'
import { flushPromises, shallowMount } from '@vue/test-utils'
import SystemSettingsView from './SystemSettingsView.vue'
import { api } from '../../lib/api'
import { mockApi } from '../../lib/mockApi'

let leaveGuard: (() => boolean | Promise<boolean>) | undefined
let canManage = true

vi.mock('../../lib/api', () => ({ api: vi.fn(), jsonBody: (value: unknown) => JSON.stringify(value) }))
vi.mock('../../composables/usePermission', () => ({ usePermission: () => ({ can: () => canManage }) }))
vi.mock('vue-router', () => ({
  onBeforeRouteLeave: (guard: () => boolean | Promise<boolean>) => { leaveGuard = guard },
}))

const SlotWrapper = defineComponent({
  name: 'SlotWrapper',
  props: ['title', 'type'],
  template: '<div><span v-if="title">{{ title }}</span><slot /></div>',
})
const Dialog = defineComponent({
  name: 'VDialog',
  props: ['modelValue'],
  template: '<div v-if="modelValue" class="qa-dialog"><slot /></div>',
})
const Button = defineComponent({
  name: 'VBtn',
  props: ['disabled', 'loading', 'type'],
  emits: ['click'],
  template: '<button :type="type || `button`" :disabled="disabled || loading" @click="$emit(`click`)"><slot /></button>',
})
const TextField = defineComponent({
  name: 'VTextField',
  props: ['modelValue', 'label'],
  emits: ['update:modelValue'],
  template: '<label>{{ label }}<input :value="modelValue" @input="$emit(`update:modelValue`, Number($event.target.value))" /></label>',
})

function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>(done => { resolve = done })
  return { promise, resolve }
}

function mountView() {
  return shallowMount(SystemSettingsView, {
    global: {
      stubs: {
        RouterLink: SlotWrapper,
        PageHeader: SlotWrapper,
        StatusPill: SlotWrapper,
        VAlert: SlotWrapper,
        VProgressLinear: true,
        VIcon: true,
        VChip: SlotWrapper,
        VSpacer: true,
        VCard: SlotWrapper,
        VCardTitle: SlotWrapper,
        VCardText: SlotWrapper,
        VCardActions: SlotWrapper,
        VDialog: Dialog,
        VBtn: Button,
        VTextField: TextField,
        VCombobox: SlotWrapper,
        VSwitch: SlotWrapper,
      },
    },
  })
}

const delivery = { batchSize: 20, leaseSeconds: 120, maximumAttempts: 5, pollIntervalSeconds: 5 }

beforeEach(() => {
  canManage = true
  leaveGuard = undefined
  vi.mocked(api).mockReset()
  vi.mocked(api).mockImplementation(async path => {
    if (path.startsWith('/settings/resources')) return mockApi(path) as never
    if (path === '/settings/delivery') return { ...delivery } as never
    if (path === '/settings/smtp') return { commandTimeoutSeconds: 300 } as never
    if (path === '/settings/relay/status') return { connected: false, activeStreams: 0, lastConnected: null, lastError: null, processes: [] } as never
    if (path === '/settings/relay') return { host: '', port: 18443, pairingHost: '', pairingPort: 18444,
      receiveEnabled: false, sendEnabled: false, paired: false, pairingPending: false,
      peerFingerprint: null, pendingExpiresAt: null,
      maxInboundConnections: 32, maxConnectionsPerIp: 4, idleTimeoutSeconds: 120 } as never
    if (path === '/settings/mailboxes') return { defaultFolders: ['INBOX', 'Sent', 'Drafts', 'Trash', 'Junk', 'Archives'] } as never
    if (path === '/settings/certificates') return {
      defaultMailHostName: null,
      allowSingleCertificateFallback: false,
      availableMailHostNames: ['mail.example.test'],
      effectiveDefaultMailHostName: null,
    } as never
    throw new Error(`unexpected request ${path}`)
  })
})

afterEach(() => {
  vi.restoreAllMocks()
})

describe('system settings safeguards', () => {
  it('saves relay capacity on the main server settings page', async () => {
    const previous = vi.mocked(api).getMockImplementation()!
    vi.mocked(api).mockImplementation(async (path, init) => {
      if (path === '/settings/relay') {
        const saved = init?.method === 'PUT' ? JSON.parse(String(init.body)) as Record<string, unknown> : {}
        return { host: 'relay.example.test', port: 18443, pairingHost: 'root.example.test', pairingPort: 18444,
          receiveEnabled: false, sendEnabled: false, paired: false, pairingPending: false,
          peerFingerprint: null, pendingExpiresAt: null,
          maxInboundConnections: 32, maxConnectionsPerIp: 4, idleTimeoutSeconds: 120, ...saved } as never
      }
      return previous(path, init)
    })
    const wrapper = mountView()
    await flushPromises()
    await wrapper.findAll('.settings-nav button').find(x => x.text().includes('邮件中继'))!.trigger('click')
    for (const [label, value] of [
      ['中继收信总连接上限', 24], ['每个来源 IP 连接上限', 3], ['无数据活动超时', 90],
    ] as const) {
      wrapper.findAllComponents(TextField).find(x => x.props('label') === label)!.vm.$emit('update:modelValue', value)
    }
    await nextTick()
    await wrapper.findAll('button').find(x => x.text() === '保存当前设置')!.trigger('click')
    await flushPromises()
    const save = vi.mocked(api).mock.calls.find(([path, init]) => path === '/settings/relay' && init?.method === 'PUT')!
    expect(JSON.parse(String(save[1]?.body))).toMatchObject({
      maxInboundConnections: 24, maxConnectionsPerIp: 3, idleTimeoutSeconds: 90,
    })
    wrapper.unmount()
  })

  it('shows reported service health without exposing internal versions', async () => {
    const previous = vi.mocked(api).getMockImplementation()!
    vi.mocked(api).mockImplementation(async (path, init) => {
      if (path === '/settings/resources/status') return {
        totalCacheBudgetBytes: 8388608, totalEstimatedCacheBytes: 1024,
        profileId: '1gb', values: null,
        processes: [{ processId: 'host:1', instanceName: 'host', roles: ['smtp', 'imap'],
          lastSeenAt: new Date().toISOString(), estimatedCacheBytes: 1024, state: 'applied', message: null,
          usage: { profileId: '1gb', cacheBudgetBytes: 8388608, deliveryLimit: 2,
            smtpLimit: 16, imapLimit: 32, activeDeliveries: 0, activeSmtp: 1, activeImap: 2,
            smtpMessageLimit: 2, activeSmtpMessages: 1,
            serviceStates: { smtp: 'listening', imap: 'listening' } } }],
      } as never
      return previous(path, init)
    })
    const wrapper = mountView()
    await flushPromises()
    await wrapper.findAll('.settings-nav button').find(x => x.text().includes('资源与性能'))!.trigger('click')
    expect(wrapper.text()).toContain('SMTP 服务状态')
    expect(wrapper.text()).toContain('IMAP 服务状态')
    expect(wrapper.text()).toContain('正在监听')
    expect(wrapper.text()).toContain('活跃邮件处理 / 上限')
    expect(wrapper.text()).not.toContain('规则版本')
    wrapper.unmount()
  })

  it('fills a preset, permits custom values, supports restore and undo, then saves all values', async () => {
    const previous = vi.mocked(api).getMockImplementation()!
    vi.mocked(api).mockImplementation(async (path, init) => {
      if (path.startsWith('/settings/resources')) return mockApi(path, init) as never
      return previous(path, init)
    })
    const wrapper = mountView()
    await flushPromises()
    await wrapper.findAll('.settings-nav button').find(x => x.text().includes('资源与性能'))!.trigger('click')
    expect(wrapper.findAll('[role="radio"]')).toHaveLength(7)
    expect(wrapper.text()).not.toContain('命中统计')
    expect(wrapper.text()).not.toContain('规则版本')
    await wrapper.findAll('[role="radio"]').find(x => x.text().includes('16 GB'))!.trigger('click')
    expect(wrapper.find('.cache-summary').text()).toContain('128.00 MiB')
    const cacheField = wrapper.findAllComponents(TextField).find(x => x.props('label') === '配置缓存总预算（MiB）')!
    cacheField.vm.$emit('update:modelValue', 96)
    await nextTick()
    expect(wrapper.text()).toContain('自定义（基于大型 · 16 GB）')
    await wrapper.findAll('button').find(x => x.text() === '恢复该预设')!.trigger('click')
    expect(wrapper.find('.cache-summary').text()).toContain('128.00 MiB')
    expect(wrapper.text()).toContain('当前分区有未保存修改')
    await wrapper.findAll('button').find(x => x.text() === '撤销当前修改')!.trigger('click')
    expect(wrapper.text()).not.toContain('当前分区有未保存修改')
    await wrapper.findAll('[role="radio"]').find(x => x.text().includes('16 GB'))!.trigger('click')
    await wrapper.findAll('button').find(x => x.text() === '保存当前设置')!.trigger('click')
    await flushPromises()
    expect(api).toHaveBeenCalledWith('/settings/resources', { method: 'PUT', body: JSON.stringify({ profileId: '16gb', values: {
      cacheBytes: 134217728, entryBytes: 16777216, deliveryConcurrency: 32, smtpConnections: 2048,
      imapConnections: 512, versionPollSeconds: 3, absoluteRefreshSeconds: 60,
      maximumConcurrentLoads: 2, ruleFallbackBatchSize: 64,
    } }) })
    expect(wrapper.text()).toContain('资源与性能已保存')
    wrapper.unmount()
  })

  it('keeps resource editing available when status reporting fails', async () => {
    const previous = vi.mocked(api).getMockImplementation()!
    vi.mocked(api).mockImplementation(async (path, init) => {
      if (path === '/settings/resources/status') throw new Error('database details must not be displayed')
      return previous(path, init)
    })
    const wrapper = mountView()
    await flushPromises()
    await wrapper.findAll('.settings-nav button').find(x => x.text().includes('资源与性能'))!.trigger('click')
    expect(wrapper.findAll('[role="radio"]')).toHaveLength(7)
    expect(wrapper.text()).toContain('暂时无法获取服务状态')
    expect(wrapper.text()).not.toContain('database details')
    wrapper.unmount()
  })

  it('does not allow read-only administrators to change the resource profile', async () => {
    canManage = false
    const wrapper = mountView()
    await flushPromises()
    await wrapper.findAll('.settings-nav button').find(x => x.text().includes('资源与性能'))!.trigger('click')
    expect(wrapper.findAll('[role="radio"]').every(x => x.attributes('disabled') !== undefined)).toBe(true)
    expect(wrapper.findAll('button').some(x => x.text() === '保存当前设置')).toBe(false)
    wrapper.unmount()
  })

  it('uses an in-page confirmation dialog when leaving with unsaved settings', async () => {
    const confirm = vi.spyOn(globalThis, 'confirm')
    const wrapper = mountView()
    await flushPromises()

    wrapper.findAllComponents(TextField)[0]!.vm.$emit('update:modelValue', 30)
    await nextTick()
    const result = leaveGuard!()
    await nextTick()

    expect(confirm).not.toHaveBeenCalled()
    expect(wrapper.find('.qa-dialog').text()).toContain('1 个设置分区尚未保存')
    await wrapper.find('.qa-dialog').findAll('button').find(button => button.text() === '继续编辑')!.trigger('click')
    await expect(result).resolves.toBe(false)
    wrapper.unmount()
  })

  it('locks section navigation while saving and keeps the success label tied to the saved section', async () => {
    const pendingSave = deferred<typeof delivery>()
    vi.mocked(api).mockImplementation(async (path, init) => {
      if (path.startsWith('/settings/resources')) return mockApi(path, init) as never
      if (path === '/settings/delivery' && init?.method === 'PUT') return pendingSave.promise as never
      if (path === '/settings/delivery') return { ...delivery } as never
      if (path === '/settings/smtp') return { commandTimeoutSeconds: 300 } as never
      if (path === '/settings/relay/status') return { connected: false, activeStreams: 0, lastConnected: null, lastError: null, processes: [] } as never
      if (path === '/settings/relay') return { host: '', port: 18443, pairingHost: '', pairingPort: 18444,
        receiveEnabled: false, sendEnabled: false, paired: false, pairingPending: false,
        peerFingerprint: null, pendingExpiresAt: null,
        maxInboundConnections: 32, maxConnectionsPerIp: 4, idleTimeoutSeconds: 120 } as never
      if (path === '/settings/mailboxes') return { defaultFolders: ['INBOX', 'Sent', 'Drafts', 'Trash', 'Junk', 'Archives'] } as never
      return { defaultMailHostName: null, allowSingleCertificateFallback: false, availableMailHostNames: [], effectiveDefaultMailHostName: null } as never
    })
    const wrapper = mountView()
    await flushPromises()
    wrapper.findAllComponents(TextField)[0]!.vm.$emit('update:modelValue', 30)
    await nextTick()

    await wrapper.findAll('button').find(button => button.text() === '保存当前设置')!.trigger('click')
    await nextTick()
    const smtpButton = wrapper.findAll('.settings-nav button').find(button => button.text().includes('SMTP 会话'))!
    expect(smtpButton.attributes('disabled')).toBeDefined()
    expect(wrapper.find('.settings-nav button.active').text()).toContain('投递调度')

    pendingSave.resolve({ ...delivery, batchSize: 30 })
    await flushPromises()
    expect(wrapper.text()).toContain('投递调度已保存')
    expect(wrapper.text()).not.toContain('SMTP 会话已保存')
    wrapper.unmount()
  })

  it('protects browser refresh after a setting changes', async () => {
    const wrapper = mountView()
    await flushPromises()
    wrapper.findAllComponents(TextField)[0]!.vm.$emit('update:modelValue', 30)
    await nextTick()

    const event = new Event('beforeunload', { cancelable: true })
    globalThis.dispatchEvent(event)
    expect(event.defaultPrevented).toBe(true)
    wrapper.unmount()
  })
})
