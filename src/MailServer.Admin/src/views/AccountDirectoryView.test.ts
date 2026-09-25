import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, reactive } from 'vue'
import { flushPromises, shallowMount, type VueWrapper } from '@vue/test-utils'
import AccountDirectoryView from './AccountDirectoryView.vue'
import AccountSettingsView from './AccountSettingsView.vue'
import DirectoryAliases from '../components/DirectoryAliases.vue'
import { api } from '../lib/api'

const navigation = vi.hoisted(() => ({ push: vi.fn(), replace: vi.fn(), leave: vi.fn(), update: vi.fn() }))
let route = reactive({ name: 'account-directory', params: { id: 'domain', accountId: 'account' }, query: {} as Record<string, string>, fullPath: '/domains/domain/accounts' })
vi.mock('vue-router', () => ({ useRoute: () => route, useRouter: () => navigation, onBeforeRouteLeave: navigation.leave, onBeforeRouteUpdate: navigation.update }))
vi.mock('../lib/api', () => ({ api: vi.fn(), jsonBody: JSON.stringify }))
const Slot = defineComponent({ props: ['title'], template: '<section>{{ title }}<slot /></section>' })
const Field = defineComponent({ props: ['modelValue', 'label', 'disabled', 'readonly'], emits: ['update:modelValue'], template: '<label>{{ label }}<input :aria-label="label" :value="modelValue" :disabled="disabled" :readonly="readonly" @input="$emit(`update:modelValue`, $event.target.value)" /></label>' })
const Button = defineComponent({ props: ['disabled', 'loading', 'to'], emits: ['click'], template: '<button :disabled="disabled || loading" @click="$emit(`click`)"><slot /></button>' })
const Dialog = defineComponent({ props: ['modelValue'], template: '<aside v-if="modelValue"><slot /></aside>' })
const stubs = { PageHeader: Slot, VCard: Slot, VCardText: Slot, VCardActions: Slot, VAlert: Slot, VTable: Slot, VChip: Slot, VTextField: Field, VAutocomplete: Field, VSelect: Field, VBtn: Button, VDialog: Dialog, VPagination: true, VCheckboxBtn: true, VSwitch: true, VProgressLinear: true, DirectoryAliases: true }
const context = { domainName: 'test.invalid', canReadAccounts: true, canReadAliases: true, canManageAccounts: true, canManageAliases: true }
const account = { id: 'account', address: 'owner@test.invalid', isEnabled: true, quotaBytes: 1073741824, usedBytes: 0, aliasCount: 2, matchedAlias: 'sales@test.invalid', matchedAliasCount: 2 }
const paged = (items: unknown[]) => ({ items, totalCount: items.length, page: 1, pageSize: 20 })
const mounted: VueWrapper[] = []
function mount(component: typeof AccountDirectoryView | typeof AccountSettingsView | typeof DirectoryAliases, props = {}) {
  const wrapper = shallowMount(component, { props, global: { stubs } })
  mounted.push(wrapper)
  return wrapper
}
function button(wrapper: VueWrapper, text: string) { return wrapper.findAll('button').find(x => x.text() === text)! }
beforeEach(() => {
  route = reactive({ name: 'account-directory', params: { id: 'domain', accountId: 'account' }, query: {}, fullPath: '/domains/domain/accounts' })
  vi.clearAllMocks()
  vi.mocked(api).mockImplementation(async path => {
    if (String(path).endsWith('/account-directory')) return { ...context } as never
    if (String(path).includes('/account-directory/accounts/')) return { ...account } as never
    return paged([]) as never
  })
})
afterEach(() => mounted.splice(0).forEach(w => w.unmount()))

describe('account and alias directory', () => {
  it('ignores out-of-order results and displays the matching aliases once per account', async () => {
    let resolveOld!: (value: unknown) => void
    const old = new Promise(resolve => { resolveOld = resolve })
    vi.mocked(api).mockImplementation(async path => {
      if (String(path).endsWith('/account-directory')) return context as never
      return (String(path).includes('query=new') ? paged([account]) : old) as never
    })
    const wrapper = mount(AccountDirectoryView)
    await flushPromises()
    route.query = { query: 'new' }; route.fullPath += '?query=new'
    await flushPromises()
    expect(wrapper.text()).toContain('匹配别名：sales@test.invalid')
    expect(wrapper.findAll('strong')).toHaveLength(1)
    resolveOld(paged([{ ...account, address: 'stale@test.invalid' }]))
    await flushPromises()
    expect(wrapper.text()).not.toContain('stale@test.invalid')
    await button(wrapper, '配置').trigger('click')
    expect(navigation.push).toHaveBeenCalledWith(expect.objectContaining({ name: 'account-settings', query: { query: 'new' } }))
  })
  it('restores URL filters and hides sensitive account controls for alias-only users', async () => {
    route.query = { query: 'sales', enabled: 'false', page: '2' }
    vi.mocked(api).mockImplementation(async path => String(path).endsWith('/account-directory') ? { ...context, canReadAccounts: false, canManageAccounts: false } as never : { ...paged([account]), totalCount: 30 } as never)
    const wrapper = mount(AccountDirectoryView)
    await flushPromises()
    expect(vi.mocked(api).mock.calls.some(([path]) => String(path).includes('page=2&pageSize=20&query=sales'))).toBe(true)
    expect(wrapper.text()).not.toContain('已用容量 / 配额')
    expect(wrapper.text()).not.toContain('批量启用')
    expect(wrapper.text()).not.toContain('新建账号')
  })
  it('loads independent account details and retains settings when a save fails', async () => {
    route.name = 'account-settings'
    const wrapper = mount(AccountSettingsView)
    await flushPromises()
    expect(wrapper.find('input[aria-label="邮箱地址"]').attributes('readonly')).toBeDefined()
    await wrapper.find('input[aria-label="容量配额（GiB）"]').setValue('3')
    vi.mocked(api).mockRejectedValueOnce(new Error('保存失败'))
    await button(wrapper, '保存账号设置').trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('保存失败')
    expect((wrapper.find('input[aria-label="容量配额（GiB）"]').element as HTMLInputElement).value).toBe('3')
    expect(button(wrapper, '保存账号设置').attributes('disabled')).toBeUndefined()
  })
  it('creates an account in the independent page and continues to its configuration', async () => {
    route.name = 'account-create'
    route.query = { query: 'owner', page: '2' }
    const wrapper = mount(AccountSettingsView)
    await flushPromises()
    await wrapper.find('input[aria-label="邮箱本地部分"]').setValue('new-owner')
    await wrapper.find('input[aria-label="初始密码（至少 12 个字符）"]').setValue('NewPassword_2026')
    await wrapper.find('input[aria-label="再次输入密码"]').setValue('NewPassword_2026')
    vi.mocked(api).mockResolvedValueOnce({ ...account, id: 'created-account' } as never)
    await button(wrapper, '创建账号').trigger('click'); await flushPromises()
    expect(navigation.replace).toHaveBeenCalledWith({ name: 'account-settings', params: { id: 'domain', accountId: 'created-account' }, query: { query: 'owner', page: '2' } })
    expect((wrapper.find('input[aria-label="初始密码（至少 12 个字符）"]').element as HTMLInputElement).value).toBe('')
  })
  it('guards query-only navigation while an alias draft is unsaved', async () => {
    route.name = 'account-settings'
    const wrapper = mount(AccountSettingsView)
    await flushPromises()
    await wrapper.find('input[aria-label="容量配额（GiB）"]').setValue('3')
    const allowed = navigation.update.mock.calls[0]![0]() as Promise<boolean>
    await flushPromises()
    expect(wrapper.text()).toContain('放弃未保存的修改？')
    await button(wrapper, '继续编辑').trigger('click')
    expect(await allowed).toBe(false)
    expect((wrapper.find('input[aria-label="容量配额（GiB）"]').element as HTMLInputElement).value).toBe('3')
  })
  it('requires matching passwords, blocks duplicate submission and clears successful passwords', async () => {
    route.name = 'account-settings'
    const wrapper = mount(AccountSettingsView)
    await flushPromises()
    await wrapper.find('input[aria-label="新密码（至少 12 个字符）"]').setValue('NewPassword_2026')
    await wrapper.find('input[aria-label="再次输入新密码"]').setValue('different')
    expect(button(wrapper, '更新密码').attributes('disabled')).toBeDefined()
    await wrapper.find('input[aria-label="再次输入新密码"]').setValue('NewPassword_2026')
    let finish!: () => void
    vi.mocked(api).mockImplementationOnce(() => new Promise(resolve => { finish = () => resolve(undefined as never) }))
    await button(wrapper, '更新密码').trigger('click')
    await button(wrapper, '更新密码').trigger('click')
    expect(vi.mocked(api).mock.calls.filter(([path]) => String(path).endsWith('/password'))).toHaveLength(1)
    finish(); await flushPromises()
    expect((wrapper.find('input[aria-label="新密码（至少 12 个字符）"]').element as HTMLInputElement).value).toBe('')
  })
  it('defaults new aliases to the current account and preserves a failed inline draft', async () => {
    const wrapper = mount(DirectoryAliases, { domainId: 'domain', accountId: 'account', accountAddress: account.address, canManage: true })
    await flushPromises()
    await button(wrapper, '添加别名').trigger('click'); await flushPromises()
    expect((wrapper.find('input[aria-label="目标账号"]').element as HTMLInputElement).value).toBe('account')
    await wrapper.find('input[aria-label="别名本地部分"]').setValue('sales')
    vi.mocked(api).mockRejectedValueOnce(new Error('地址已存在'))
    await button(wrapper, '保存别名').trigger('click'); await flushPromises()
    expect(wrapper.text()).toContain('地址已存在')
    expect((wrapper.find('input[aria-label="别名本地部分"]').element as HTMLInputElement).value).toBe('sales')
    await button(wrapper, '取消').trigger('click')
    expect(wrapper.find('aside').exists()).toBe(true)
    await button(wrapper, '继续编辑').trigger('click')
    expect(wrapper.find('input[aria-label="别名本地部分"]').exists()).toBe(true)
  })
  it('refreshes the alias list after moving an alias to another account', async () => {
    const alias = { id: 'alias', source: 'sales@test.invalid', targetAccountId: 'account', targetAddress: account.address, mode: 'SendReceive', unavailableReason: null }
    vi.mocked(api).mockResolvedValue(paged([alias]) as never)
    const wrapper = mount(DirectoryAliases, { domainId: 'domain', accountId: 'account', accountAddress: account.address, canManage: true })
    await flushPromises()
    await button(wrapper, '编辑').trigger('click'); await flushPromises()
    await wrapper.find('input[aria-label="目标账号"]').setValue('second')
    vi.mocked(api).mockResolvedValue(paged([]) as never)
    await button(wrapper, '保存别名').trigger('click'); await flushPromises()
    expect(vi.mocked(api)).toHaveBeenCalledWith('/domains/domain/aliases/alias', expect.objectContaining({ method: 'PUT', body: expect.stringContaining('second') }))
    expect(wrapper.text()).toContain('没有符合条件的别名')
    expect(wrapper.emitted('changed')).toHaveLength(1)
  })
})
