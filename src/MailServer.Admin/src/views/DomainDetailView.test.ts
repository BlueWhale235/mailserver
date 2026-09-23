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
  onBeforeRouteLeave: vi.fn(),
  onBeforeRouteUpdate: vi.fn(),
}))
vi.mock('../composables/usePermission', () => ({ usePermission: () => ({ can: () => true }) }))

const domainId = 'domain-1'
const alias = {
  id: 'alias-1', source: 'sales@example.test', targetAccountId: 'acc-1',
  targetAddress: 'billing@example.test', isEnabled: true, mode: 'SendReceive', canReceive: true, canSend: true,
}
const disabledAccount = {
  id: 'acc-disabled', address: 'archive@example.test', isEnabled: false,
  quotaBytes: 10 * 1024 ** 3, usedBytes: 1024 ** 2, createdAt: '2026-01-02T00:00:00Z', aliasCount: 2,
}
const enabledAccount = {
  id: 'acc-enabled', address: 'active@example.test', isEnabled: true,
  quotaBytes: 10 * 1024 ** 3, usedBytes: 0, createdAt: '2026-01-03T00:00:00Z', aliasCount: 0,
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
const Tabs = defineComponent({
  name: 'VTabs',
  props: ['modelValue'],
  emits: ['update:modelValue'],
  template: '<div><button class="qa-tab-overview" @click="$emit(\'update:modelValue\', \'overview\')">overview</button><button class="qa-tab-quota" @click="$emit(\'update:modelValue\', \'quota\')">quota</button><slot /></div>',
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
        VTabs: Tabs, VTab: SlotWrapper, VPagination: Pagination,
        VTextField: TextField, VSelect: SlotWrapper, VSwitch: SlotWrapper, VTextarea: SlotWrapper,
      },
    },
  })
}

const deleteCalls = () => vi.mocked(api).mock.calls
  .filter(c => String(c[0]).includes('/aliases/') && (c[1] as { method?: string } | undefined)?.method === 'DELETE')
const batchCalls = () => vi.mocked(api).mock.calls.filter(c => String(c[0]).includes('batch-delete'))
const accountDeleteCalls = () => vi.mocked(api).mock.calls
  .filter(c => String(c[0]).startsWith('/accounts/') && (c[1] as { method?: string } | undefined)?.method === 'DELETE')

function provideAccounts(rows = [disabledAccount]) {
  const original = vi.mocked(api).getMockImplementation()!
  vi.mocked(api).mockImplementation((path, init) => {
    if (String(path).startsWith(`/domains/${domainId}/accounts/search`)) return Promise.resolve(paged(rows)) as never
    if (path === `/domains/${domainId}/accounts`) return Promise.resolve(rows) as never
    return original(path, init)
  })
}

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
  it.each(['resolve', 'reject'])('ignores an obsolete first-page response (%s) after page two loads', async (completion) => {
    const original = vi.mocked(api).getMockImplementation()!
    let resolveOld!: (value: unknown) => void
    let rejectOld!: (reason: Error) => void
    vi.mocked(api).mockImplementation((path, init) => {
      if (String(path).includes('/aliases/search')) {
        if (String(path).includes('page=2')) return Promise.resolve({ ...paged([{ ...alias, source: 'page-two@example.test' }]), totalCount: 45, page: 2 }) as never
        return new Promise((resolve, reject) => { resolveOld = resolve; rejectOld = reject }) as never
      }
      return original(path, init)
    })
    const wrapper = mountView()
    await flushPromises()
    wrapper.findComponent({ name: 'VPagination' }).vm.$emit('update:modelValue', 2)
    await flushPromises()
    expect(wrapper.text()).toContain('page-two@example.test')
    if (completion === 'resolve') resolveOld({ ...paged([alias]), totalCount: 45 })
    else rejectOld(new Error('obsolete request failed'))
    await flushPromises()
    expect(wrapper.text()).toContain('page-two@example.test')
    expect(wrapper.text()).not.toContain('obsolete request failed')
    expect(wrapper.text()).not.toContain('sales@example.test')
  })

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

    await wrapper.findAll('button').find(b => b.text().trim() === '取消')!.trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('放弃域基础配置修改？')
    expect(wrapper.text()).toContain('本次修改将无法恢复')

    await wrapper.findAll('button').find(b => b.text().trim() === '继续编辑')!.trigger('click')
    await flushPromises()
    expect(wrapper.text()).not.toContain('放弃域基础配置修改？')
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

describe('domain account deletion safeguards', () => {
  it('opens a recoverable soft-delete confirmation without sending a request', async () => {
    provideAccounts()
    const wrapper = mountView('accounts')
    await flushPromises()

    await wrapper.findAll('button').find(button => button.attributes('aria-label') === '删除账号')!.trigger('click')
    await flushPromises()

    expect(accountDeleteCalls()).toHaveLength(0)
    expect(wrapper.text()).toContain('确认软删除邮箱账号')
    expect(wrapper.text()).toContain('archive@example.test')
    expect(wrapper.text()).toContain('回收站')
    expect(wrapper.text()).toContain('关联 2 个别名')
  })

  it('cancels or confirms a single account deletion explicitly', async () => {
    provideAccounts()
    const wrapper = mountView('accounts')
    await flushPromises()
    const deleteButton = wrapper.findAll('button').find(button => button.attributes('aria-label') === '删除账号')!

    await deleteButton.trigger('click')
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text().trim() === '取消')!.trigger('click')
    await flushPromises()
    expect(accountDeleteCalls()).toHaveLength(0)

    await deleteButton.trigger('click')
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text().trim() === '确认软删除')!.trigger('click')
    await flushPromises()
    expect(accountDeleteCalls()).toHaveLength(1)
    expect(accountDeleteCalls()[0]?.[0]).toBe('/accounts/acc-disabled')
    expect(wrapper.text()).toContain('可在回收站恢复')
  })

  it('keeps a failed single-account deletion in the dialog for retry', async () => {
    provideAccounts()
    const original = vi.mocked(api).getMockImplementation()!
    vi.mocked(api).mockImplementation((path, init) => {
      if (path === '/accounts/acc-disabled' && init?.method === 'DELETE') return Promise.reject(new Error('账号仍被后台任务占用')) as never
      return original(path, init)
    })
    const wrapper = mountView('accounts')
    await flushPromises()

    await wrapper.findAll('button').find(button => button.attributes('aria-label') === '删除账号')!.trigger('click')
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text().trim() === '确认软删除')!.trigger('click')
    await flushPromises()

    expect(wrapper.find('.qa-dialog').exists()).toBe(true)
    expect(wrapper.text()).toContain('账号仍被后台任务占用')
    expect(wrapper.text()).toContain('archive@example.test')
  })

  it('blocks a batch deletion that contains an enabled account', async () => {
    provideAccounts([disabledAccount, enabledAccount])
    const wrapper = mountView('accounts')
    await flushPromises()

    await wrapper.findAllComponents(Checkbox)[0]!.trigger('click')
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text().trim() === '批量删除')!.trigger('click')
    await flushPromises()

    expect(batchCalls()).toHaveLength(0)
    expect(wrapper.text()).toContain('请取消后先执行“批量停用”')
    expect(wrapper.text()).toContain('active@example.test（运行中，不能删除）')
    expect(wrapper.findAll('button').find(button => button.text().includes('确认软删除'))!.attributes('disabled')).toBeDefined()
  })

  it('batch-deletes the frozen disabled account targets only after confirmation', async () => {
    provideAccounts([disabledAccount, { ...disabledAccount, id: 'acc-second', address: 'second@example.test' }])
    const wrapper = mountView('accounts')
    await flushPromises()

    await wrapper.findAllComponents(Checkbox)[0]!.trigger('click')
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text().trim() === '批量删除')!.trigger('click')
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text().trim() === '确认软删除 2 个')!.trigger('click')
    await flushPromises()

    expect(batchCalls()).toHaveLength(1)
    expect(JSON.parse(String(batchCalls()[0]?.[1]?.body))).toEqual({ ids: ['acc-disabled', 'acc-second'] })
    expect(wrapper.text()).toContain('已软删除 2 个邮箱账号')
  })
})

describe('domain DNS unsaved-change protection', () => {
  it('keeps a dirty DNS draft until the administrator confirms leaving the tab', async () => {
    const wrapper = mountView('dns')
    await flushPromises()

    wrapper.findComponent({ name: 'DomainDnsWorkspace' }).vm.$emit('dirty-changed', true)
    await flushPromises()
    await wrapper.find('.qa-tab-quota').trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('放弃“DNS 安全策略”修改？')
    expect(wrapper.text()).toContain('本次修改将无法恢复')

    await wrapper.findAll('button').find(button => button.text().trim() === '继续编辑')!.trigger('click')
    await flushPromises()
    expect(wrapper.text()).not.toContain('放弃 DNS 策略修改？')

    await wrapper.find('.qa-tab-quota').trigger('click')
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text().trim() === '放弃修改并离开')!.trigger('click')
    await flushPromises()
    expect(wrapper.text()).not.toContain('放弃 DNS 策略修改？')
  })

  it('blocks abandoning the DNS tab while a save request is running', async () => {
    const wrapper = mountView('dns')
    await flushPromises()
    const dns = wrapper.findComponent({ name: 'DomainDnsWorkspace' })
    dns.vm.$emit('dirty-changed', true)
    dns.vm.$emit('saving-changed', true)
    await flushPromises()

    await wrapper.find('.qa-tab-quota').trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('正在保存，请等待保存完成后再离开')
    expect(wrapper.findAll('button').find(button => button.text().trim() === '放弃修改并离开')!.attributes('disabled')).toBeDefined()
  })

  it('protects certificate secrets before leaving the certificate tab', async () => {
    const wrapper = mountView('cert')
    await flushPromises()
    wrapper.findComponent({ name: 'DomainCertificateWorkspace' }).vm.$emit('dirty-changed', true)
    await flushPromises()

    await wrapper.find('.qa-tab-overview').trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('放弃“证书配置”修改？')
    expect(wrapper.text()).toContain('当前证书配置尚未保存')
  })

  it('protects an edited domain quota and restores it when changes are discarded', async () => {
    const wrapper = mountView('quota')
    await flushPromises()
    const quota = wrapper.findAllComponents({ name: 'VTextField' })
      .find(field => field.props('label') === '新配额（GiB）')!
    await quota.find('input').setValue('2048')
    await flushPromises()

    await wrapper.find('.qa-tab-overview').trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('放弃“域存储配额”修改？')

    await wrapper.findAll('button').find(button => button.text().trim() === '放弃修改并离开')!.trigger('click')
    await flushPromises()
    expect(wrapper.text()).not.toContain('放弃“域存储配额”修改？')
  })
})
