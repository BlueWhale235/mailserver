import { afterEach, describe, expect, it, vi } from 'vitest'
import { defineComponent } from 'vue'
import { flushPromises, shallowMount } from '@vue/test-utils'
import OperationsView from './OperationsView.vue'
import { api } from '../lib/api'

let granted = ['operations.mail.read', 'settings.read', 'task.read']
vi.mock('../lib/api', () => ({ api: vi.fn(), apiDownload: vi.fn() }))
vi.mock('../stores/auth', () => ({ useAuthStore: () => ({ can: (code: string) => granted.includes(code) }) }))

const SlotWrapper = defineComponent({ name: 'SlotWrapper', props: ['title'], template: '<div><span>{{ title }}</span><slot /></div>' })
const Button = defineComponent({ name: 'VBtn', emits: ['click'], template: '<button @click="$emit(`click`)"><slot /></button>' })
const Dialog = defineComponent({ name: 'VDialog', props: ['modelValue'], template: '<div v-if="modelValue"><slot /></div>' })

function mountView() {
  return shallowMount(OperationsView, { global: { stubs: {
    PageHeader: SlotWrapper, VTabs: SlotWrapper, VTab: SlotWrapper, VCard: SlotWrapper, VCardText: SlotWrapper,
    VCardTitle: SlotWrapper, VCardActions: SlotWrapper, VTable: SlotWrapper, VAlert: SlotWrapper,
    VBtn: Button, VDialog: Dialog, VSpacer: true, VTextField: true, VSelect: true,
  } } })
}

afterEach(() => { vi.resetAllMocks(); granted = ['operations.mail.read', 'settings.read', 'task.read'] })

describe('operations center', () => {
  it('starts with a direct database-backed mail query and keeps content controls permission-gated', async () => {
    granted = ['operations.mail.read']
    vi.mocked(api).mockImplementation(async path => {
      if (String(path).startsWith('/operations/messages?')) return { items: [{ id: 'mail-1', internetMessageId: 'case@example.test', envelopeFrom: 'actual@example.test', receivedAt: '2026-09-28T00:00:00Z', mailboxCopies: 1, recycledCopies: 0, deliveryJobs: 0, quarantinedCopies: 0 }], totalCount: 1 } as never
      if (path === '/operations/messages/mail-1') return { message: { id: 'mail-1', internetMessageId: 'case@example.test', envelopeFrom: 'actual@example.test' }, originalRecipients: [], mailboxCopies: [], deliveryRecipients: [], quarantineRecipients: [] } as never
      if (String(path).endsWith('/deliveries?page=1&pageSize=50')) return { items: [], totalCount: 0 } as never
      throw new Error(`Unexpected request ${path}`)
    })
    const wrapper = mountView()
    await flushPromises()
    expect(api).toHaveBeenCalledWith('/operations/messages?page=1&pageSize=50')
    expect(wrapper.text()).toContain('case@example.test')
    await wrapper.findAll('button').find(button => button.text() === '查看')!.trigger('click')
    await flushPromises()
    expect(wrapper.text()).not.toContain('预览内容')
    expect(wrapper.text()).not.toContain('下载原文')
    wrapper.unmount()
  })

  it('does not request mail when only service status is permitted', async () => {
    granted = ['settings.read']
    vi.mocked(api).mockResolvedValue({ processes: [{ processId: 'api-1', instanceName: 'host', roles: ['api'], state: 'unreported', lastSeenAt: '2026-09-27T00:00:00Z', estimatedCacheBytes: 0 }] } as never)
    const wrapper = mountView()
    await flushPromises()
    expect(api).toHaveBeenCalledWith('/settings/resources/status')
    expect(wrapper.text()).toContain('上报过期')
    expect(wrapper.text()).not.toContain('查找邮件')
    wrapper.unmount()
  })
})
