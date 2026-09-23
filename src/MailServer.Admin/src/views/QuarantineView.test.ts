import { afterEach, describe, expect, it, vi } from 'vitest'
import { defineComponent } from 'vue'
import { flushPromises, shallowMount } from '@vue/test-utils'
import QuarantineView from './QuarantineView.vue'
import { api } from '../lib/api'
import { downloadQuarantineRaw } from '../lib/quarantine'

vi.mock('../lib/api', () => ({ api: vi.fn(), jsonBody: (value: unknown) => JSON.stringify(value) }))
vi.mock('../lib/quarantine', async importOriginal => {
  const actual = await importOriginal<typeof import('../lib/quarantine')>()
  return { ...actual, downloadQuarantineRaw: vi.fn() }
})
vi.mock('../stores/auth', () => ({ useAuthStore: () => ({ can: () => true }) }))
vi.mock('vue-router', () => ({ onBeforeRouteLeave: vi.fn() }))

const SlotWrapper = defineComponent({ name: 'SlotWrapper', template: '<div><slot /></div>' })
const Button = defineComponent({
  name: 'VBtn', props: ['disabled', 'loading'], emits: ['click'],
  template: '<button :disabled="disabled || loading" @click="$emit(`click`)"><slot /></button>',
})
const Dialog = defineComponent({
  name: 'VDialog', props: ['modelValue'],
  template: '<div v-if="modelValue" class="qa-dialog"><slot /></div>',
})

function mountView() {
  return shallowMount(QuarantineView, {
    global: { stubs: {
      PageHeader: SlotWrapper, StatusPill: true, VAlert: SlotWrapper, VProgressLinear: true,
      VTable: SlotWrapper, VCard: SlotWrapper, VCardText: SlotWrapper, VCardActions: SlotWrapper,
      VDialog: Dialog, VBtn: Button, VSelect: true, VTextField: true, VTextarea: true,
      VPagination: true,
    } },
  })
}

afterEach(() => {
  vi.restoreAllMocks()
})

describe('quarantine review', () => {
  it('warns when preview is truncated and downloads the complete eml', async () => {
    const record = {
      id: 'record-1', domainId: 'domain-1', messageId: 'message-1', sender: 'sender@example.net',
      recipients: ['alice@example.test'], remoteAddress: '192.0.2.1', pluginId: 'risk', pluginName: '风险扫描',
      reason: '命中策略', status: 'PendingReview', quarantinedAt: '2026-09-21T08:00:00Z', size: 7_340_032,
    }
    vi.mocked(api).mockImplementation(async path => {
      if (path === '/quarantine/domains') return [{ id: 'domain-1', name: 'example.test' }] as never
      if (String(path).endsWith('/raw')) return { record, fileName: 'quarantine-record1.eml', content: 'preview', truncated: true } as never
      return { items: [record], page: 1, pageSize: 20, total: 1 } as never
    })
    vi.mocked(downloadQuarantineRaw).mockResolvedValue('quarantine-record1.eml')

    const wrapper = mountView()
    await flushPromises()
    const review = wrapper.findAll('button').find(button => button.text().trim() === '查看与审核')!
    await review.trigger('click')
    await flushPromises()

    const dialog = wrapper.find('.qa-dialog')
    expect(dialog.text()).toContain('页面仅显示前 256K 字符')
    const download = dialog.findAll('button').find(button => button.text().includes('下载完整 .eml'))!
    await download.trigger('click')
    await flushPromises()
    expect(downloadQuarantineRaw).toHaveBeenCalledWith('domain-1', 'record-1')
    expect(wrapper.text()).toContain('完整邮件原文已下载')
    wrapper.unmount()
  })
})
