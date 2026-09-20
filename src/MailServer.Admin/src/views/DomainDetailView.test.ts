import { beforeEach, afterEach, describe, expect, it, vi } from 'vitest'
import { defineComponent } from 'vue'
import { flushPromises, shallowMount } from '@vue/test-utils'
import DomainDetailView from './DomainDetailView.vue'
import { api } from '../lib/api'

vi.mock('../lib/api', () => ({ api: vi.fn(), jsonBody: (value: unknown) => JSON.stringify(value) }))
const MockRoute = { params: { id: 'domain-1', tab: 'aliases' }, query: {} as Record<string, unknown> }
vi.mock('vue-router', () => ({
  useRoute: () => MockRoute,
  useRouter: () => ({ push: vi.fn(), replace: vi.fn() }),
}))
vi.mock('../composables/usePermission', () => ({ usePermission: () => ({ can: () => true }) }))

const domainId = 'domain-1'
const alias = {
  id: 'alias-1', source: 'sales@example.test', targetAccountId: 'acc-1',
  targetAddress: 'billing@example.test', isEnabled: true, mode: 'SendReceive', canReceive: true, canSend: true,
}

const management = {
  domain: {
    id: domainId, name: 'example.test', mailHostName: 'mail.example.test', isActive: true,
    quotaBytes: 1024 ** 4, usedBytes: 1024 ** 3, directDeliverSameDomain: true, createdAt: '2026-01-01T00:00:00Z',
  },
  certificate: null,
  dnsSettings: {},
  connections: [], outboundAddressOptions: [],
}
const health = {
  accountCount: 1, enabledAccountCount: 1, aliasCount: 1, pendingDeliveries: 0, failedDeliveries: 0,
  pendingQuarantineCount: 0, usedBytes: 0, quotaBytes: 0,
  certificateExpiresAt: null, certificateDaysRemaining: null, dnsStatus: 'Healthy', dnsCheckedAt: null, lastAuditAt: null,
}
const dnsCheck = {
  domainId, domainName: 'example.test', mailHostName: 'mail.example.test', checkedAt: '2026-01-01T00:00:00Z',
  status: 'Healthy', records: [], validation: { items: [], warnings: [] },
}

function paged(items: unknown[]) { return { items, totalCount: items.length, page: 1, pageSize: 20 } }

const SlotWrapper = defineComponent({
  name: 'SlotWrapper',
  props: { title: { type: String, default: '' } },
  template: '<div><h2 v-if="title">{{ title }}</h2><slot /></div>',
})
const Tooltip = defineComponent({
  name: 'VTooltip',
  setup(_props, { slots }) { return () => slots.activator?.({ props: {} }) ?? null },
})
const Dialog = defineComponent({
  name: 'VDialog',
  props: ['modelValue'],
  template: '<div v-if="modelValue" class="qa-dialog"><slot /></div>',
})
const Button = defineComponent({
  name: 'VBtn',
  props: ['disabled'],
  emits: ['click'],
  template: '<button :disabled="disabled" @click="$emit(\'click\')"><slot /></button>',
})
const TextField = defineComponent({
  name: 'VTextField',
  props: { modelValue: { type: [String, Number], default: '' }, label: { type: String, default: '' } },
  emits: ['update:modelValue'],
  template: '<label><span>{{ label }}</span><input :value="modelValue" @input="$emit(\'update:modelValue\', ($event.target).value)" /></label>',
})
const Pagination = defineComponent({
  name: 'VPagination',
  props: { modelValue: { type: Number, default: 1 }, length: { type: Number, default: 1 } },
  emits: ['update:modelValue'],
  template: '<div class="qa-pagination"><button class="qa-page-2" @click="$emit(\'update:modelValue\', 2)">page2</button></div>',
})
const Checkbox = defineComponent({
  name: 'VCheckboxBtn',
  props: ['modelValue'],
  emits: ['update:modelValue'],
  template: '<button class="qa-checkbox" @click="$emit(\'update:modelValue\', !modelValue)">select</button>',
})

function mountView(tab = 'aliases') {
  MockRoute.params = { id: 'domain-1', tab }
  return shallowMount(DomainDetailView, {
    global: {
      stubs: {
        RouterLink: true,
        PageHeader: SlotWrapper, StatusPill: true, DomainRuleWorkspace: true, DomainCertificateWorkspace: true,
        DomainDnsWorkspace: true, QueueWorkspace: true, VIcon: true, VSpacer: true, VProgressLinear: true,
        VTooltip: Tooltip, VAlert: SlotWrapper, VList: true,
        VCard: SlotWrapper, VCardTitle: SlotWrapper, VCardText: SlotWrapper, VCardActions: SlotWrapper,
        VTable: SlotWrapper, VDialog: Dialog, VBtn: Button, VCheckboxBtn: Checkbox,
        VTabs: SlotWrapper, VTab: SlotWrapper, VPagination: Pagination,
        VTextField: TextField, VSelect: SlotWrapper, VSwitch: SlotWrapper, VTextarea: SlotWrapper,
      },
    },
  })
}

const deleteCalls = () => vi.mocked(api).mock.calls
  .filter(c => String(c[0]).includes('/aliases/') && (c[1] as { method?: string } | undefined)?.method === 'DELETE')
const batchCalls = () => vi.mocked(api).mock.calls.filter(c => String(c[0]).includes('batch-delete'))

const mounted: Array<{ unmount: () => void }> = []

beforeEach(() => {
  vi.mocked(api).mockReset()
  vi.mocked(api).mockImplementation(async (path: string) => {
    if (path === `/domains/${domainId}`) return management as never
    if (path === `/domains/${domainId}/health`) return health as never
    if (path === `/domains/${domainId}/dns/check`) return dnsCheck as never
    if (path.startsWith(`/domains/${domainId}/aliases/search`)) return paged([alias]) as never
    if (path.startsWith(`/domains/${domainId}/accounts/search`)) return paged([]) as never
    if (path === `/domains/${domainId}/accounts`) return [] as never
    return {} as never
  })
})
afterEach(() => { mounted.splice(0).forEach(w => w.unmount()) })

describe('domain alias deletion safeguards', () => {
  it('shows the alias row with its target before any deletion', async () => {
    const wrapper = mountView()
    await flushPromises()
    expect(wrapper.text()).toContain('sales@example.test')
    expect(wrapper.text()).toContain('billing@example.test')
  })

  it('does not call the delete endpoint until the confirmation dialog is accepted', async () => {
    const wrapper = mountView()
    await flushPromises()
    expect(deleteCalls()).toHaveLength(0)

    const deleteButton = wrapper.findAll('button').find(b => b.attributes('aria-label') === '删除别名')
    expect(deleteButton).toBeTruthy()
    await deleteButton!.trigger('click')
    await flushPromises()

    expect(wrapper.find('.qa-dialog').exists()).toBe(true)
    expect(wrapper.text()).toContain('确认删除别名')
    expect(wrapper.text()).toContain('不可恢复')
    expect(deleteCalls()).toHaveLength(0)
  })

  it('deletes the alias only after the confirmation button is pressed', async () => {
    const wrapper = mountView()
    await flushPromises()
    await wrapper.findAll('button').find(b => b.attributes('aria-label') === '删除别名')!.trigger('click')
    await flushPromises()

    const confirmButton = wrapper.findAll('button').find(b => b.text().trim() === '确认删除')
    expect(confirmButton).toBeTruthy()
    await confirmButton!.trigger('click')
    await flushPromises()

    expect(deleteCalls()).toHaveLength(1)
  })

  it('does not delete anything when the confirmation dialog is dismissed', async () => {
    const wrapper = mountView()
    await flushPromises()
    await wrapper.findAll('button').find(b => b.attributes('aria-label') === '删除别名')!.trigger('click')
    await flushPromises()

    const cancelButton = wrapper.findAll('button').find(b => b.text().trim() === '取消')
    expect(cancelButton).toBeTruthy()
    await cancelButton!.trigger('click')
    await flushPromises()

    expect(deleteCalls()).toHaveLength(0)
    expect(wrapper.find('.qa-dialog').exists()).toBe(false)
  })

  it('keeps the domain edit save button disabled until the host actually changes', async () => {
    const wrapper = mountView('overview')
    await flushPromises()
    await wrapper.findAll('button').find(b => b.attributes('aria-label') === '编辑域配置')!.trigger('click')
    await flushPromises()

    const saveButton = () => wrapper.findAll('button').find(b => b.text().trim() === '保存配置')!
    expect(saveButton().attributes('disabled')).toBeDefined()

    const hostInput = wrapper.find('input')
    await hostInput.setValue('mx.example.test')
    await flushPromises()
    expect(saveButton().attributes('disabled')).toBeUndefined()

    await hostInput.setValue('mail.example.test')
    await flushPromises()
    expect(saveButton().attributes('disabled')).toBeDefined()
  })

  it('requests the page selected by the pagination control and clamps back to a valid page', async () => {
    const wrapper = mountView()
    await flushPromises()
    const searchCalls = () => vi.mocked(api).mock.calls.map(c => String(c[0])).filter(p => p.includes('/aliases/search'))
    expect(searchCalls()[0]).toContain('page=1')

    const pager = wrapper.findComponent({ name: 'VPagination' })
    expect(pager.exists()).toBe(true)

    await wrapper.find('.qa-page-2').trigger('click')
    await flushPromises()

    // 页码变化必须真正触发请求；mock 只返回 1 条，因此随后会被 clamp 回第 1 页。
    expect(searchCalls().some(path => path.includes('page=2'))).toBe(true)
    expect(searchCalls().at(-1)).toContain('page=1')
  })

  it('loads the whole domain account list for the alias target selector instead of the current page', async () => {
    mountView()
    await flushPromises()
    const calledPaths = vi.mocked(api).mock.calls.map(c => String(c[0]))
    expect(calledPaths.some(path => path === `/domains/${domainId}/accounts`)).toBe(true)
  })

  it('requires confirmation before a batch alias delete', async () => {
    const wrapper = mountView()
    await flushPromises()

    await wrapper.findAllComponents(Checkbox)[0]!.trigger('click')
    await flushPromises()

    const batchButton = wrapper.findAll('button').find(b => b.text().trim() === '批量删除')
    expect(batchButton).toBeTruthy()
    expect(batchButton!.attributes('disabled')).toBeUndefined()
    await batchButton!.trigger('click')
    await flushPromises()

    expect(batchCalls()).toHaveLength(0)
    expect(wrapper.text()).toContain('确认批量删除别名')
  })
})
