import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, reactive } from 'vue'
import { flushPromises, mount, shallowMount } from '@vue/test-utils'
import { createVuetify } from 'vuetify'
import DomainRuleWorkspace from './DomainRuleWorkspace.vue'
import { api } from '../lib/api'
import { VForm } from 'vuetify/components/VForm'
import { VTextField } from 'vuetify/components/VTextField'

vi.mock('../lib/api', () => ({ api: vi.fn(), jsonBody: (value: unknown) => JSON.stringify(value) }))

const routeParams = reactive<Record<string, unknown>>({ id: 'domain-1', tab: 'rules', section: 'incoming' })
const routeQuery = reactive<Record<string, unknown>>({})
const replace = vi.fn(async (location: { params?: Record<string, unknown> }) => {
  if (location.params?.section) routeParams.section = location.params.section
})
const leaveGuards: Array<() => unknown> = []
const updateGuards: Array<(to: { params: Record<string, unknown> }, from: { params: Record<string, unknown> }) => unknown> = []

vi.mock('vue-router', () => ({
  useRoute: () => ({ params: routeParams, query: routeQuery }),
  useRouter: () => ({ replace }),
  onBeforeRouteLeave: (guard: () => unknown) => { leaveGuards.push(guard) },
  onBeforeRouteUpdate: (guard: (to: { params: Record<string, unknown> }, from: { params: Record<string, unknown> }) => unknown) => { updateGuards.push(guard) },
}))

const SlotWrapper = defineComponent({
  name: 'SlotWrapper',
  props: { title: { type: String, default: '' } },
  template: '<div><h2 v-if="title">{{ title }}</h2><slot /></div>',
})
const Tooltip = defineComponent({ name: 'VTooltip', setup(_props, { slots }) { return () => slots.activator?.({ props: {} }) ?? null } })
const Button = defineComponent({
  name: 'VBtn',
  props: ['disabled', 'loading'], emits: ['click'],
  template: '<button :disabled="disabled || loading" @click="$emit(\'click\')"><slot /></button>',
})
const TextField = defineComponent({
  name: 'VTextField',
  props: { modelValue: { type: [String, Number], default: '' }, label: { type: String, default: '' } },
  emits: ['update:modelValue'],
  template: '<label><span>{{ label }}</span><input :value="modelValue" @input="$emit(\'update:modelValue\', ($event.target).value)" /></label>',
})
const StatusPill = defineComponent({ name: 'StatusPill', props: ['label', 'tone'], template: '<span class="qa-pill">{{ label }}</span>' })

function rulesFor(direction: 'Incoming' | 'Outgoing') {
  return [{
    id: direction === 'Incoming' ? 'rule-in' : 'rule-out', name: direction === 'Incoming' ? '收信归档' : '发信转发',
    direction, matchMode: 'All', targetMailboxName: direction === 'Incoming' ? 'INBOX' : 'Sent', isEnabled: true, priority: 10,
    conditions: [{ field: 'Sender', operator: 'EqualsAny', values: ['user@example.test'] }],
    actions: direction === 'Incoming' ? [{ kind: 'MoveToFolder', values: ['Junk'] }] : [{ kind: 'ForwardTo', values: ['audit@example.test'] }],
  }]
}

function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>(r => { resolve = r })
  return { promise, resolve }
}

const mounted: Array<{ unmount: () => void }> = []

function mountWorkspace(realInputs = false) {
  const wrapper = (realInputs ? mount : shallowMount)(DomainRuleWorkspace, {
    props: { domainId: 'domain-1', domainName: 'example.test', canManage: true },
    global: { plugins: realInputs ? [createVuetify({ components: { VForm, VTextField } })] : [], stubs: {
      StatusPill, VIcon: true, VSpacer: true, VProgressLinear: true, VAlert: SlotWrapper,
      VCard: SlotWrapper, VCardText: true, VCardActions: true, VSwitch: true,
      VBtn: Button, VTextField: realInputs ? false : TextField, VSelect: true, VCombobox: true, VTooltip: Tooltip,
      VForm: realInputs ? false : SlotWrapper,
    } },
  })
  mounted.push(wrapper)
  return wrapper
}

function apiHandler(getRules: (direction: string) => unknown) {
  return async (path: string, options?: { method?: string }) => {
    if (path.includes('/rule-folders')) return [{ name: 'Junk', availableAccounts: 1, totalAccounts: 1 }] as never
    if (path.includes('/rules?direction=')) return getRules(path.includes('Outgoing') ? 'Outgoing' : 'Incoming') as never
    if (path.includes('/rules/order')) return options as never
    if (path.includes('/rules/')) return {} as never
    return {} as never
  }
}

const rowButton = (wrapper: ReturnType<typeof mountWorkspace>, label: string) =>
  wrapper.findAll('button').find(button => button.attributes('aria-label') === label)

beforeEach(() => {
  routeParams.id = 'domain-1'
  routeParams.tab = 'rules'
  routeParams.section = 'incoming'
  replace.mockClear()
  leaveGuards.splice(0)
  updateGuards.splice(0)
  vi.mocked(api).mockReset()
  vi.mocked(api).mockImplementation(apiHandler(() => rulesFor('Incoming')))
})
afterEach(() => { mounted.splice(0).forEach(wrapper => wrapper.unmount()); vi.unstubAllGlobals() })

describe('mail rule workspace safeguards', () => {
  it('disables real Vuetify inputs during a pending save and restores the draft after failure', async () => {
    vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} })
    const wrapper = mountWorkspace(true)
    await flushPromises()
    let rejectSave!: (reason: Error) => void
    const pending = new Promise((_resolve, reject) => { rejectSave = reject })
    vi.mocked(api).mockImplementation(async (path, options) => {
      if (options?.method === 'PUT') return pending as never
      return apiHandler(() => rulesFor('Incoming'))(path, options)
    })
    await rowButton(wrapper, '编辑规则')!.trigger('click')
    await wrapper.find('input').setValue('保存失败后保留的草稿')
    await wrapper.findAll('button').find(button => button.text().trim() === '保存规则')!.trigger('click')
    await flushPromises()
    expect((wrapper.find('input').element as HTMLInputElement).disabled).toBe(true)
    expect(wrapper.find('form').attributes('aria-busy')).toBe('true')
    rejectSave(new Error('测试保存失败'))
    await flushPromises()
    expect((wrapper.find('input').element as HTMLInputElement).disabled).toBe(false)
    expect((wrapper.find('input').element as HTMLInputElement).value).toBe('保存失败后保留的草稿')
    expect(wrapper.text()).toContain('测试保存失败')
  })

  it('protects dirty drafts against page exit and removes protection after closing or unmounting', async () => {
    vi.stubGlobal('confirm', vi.fn(() => true))
    const wrapper = mountWorkspace()
    await flushPromises()
    const exitPrevented = () => {
      const event = new Event('beforeunload', { cancelable: true })
      window.dispatchEvent(event)
      return event.defaultPrevented
    }
    expect(exitPrevented()).toBe(false)
    await rowButton(wrapper, '编辑规则')!.trigger('click')
    expect(exitPrevented()).toBe(false)
    await wrapper.findComponent({ name: 'VTextField' }).setValue('未保存的新名称')
    expect(exitPrevented()).toBe(true)
    await wrapper.findAll('button').find(button => button.text().includes('返回规则列表'))!.trigger('click')
    expect(exitPrevented()).toBe(false)
    await rowButton(wrapper, '编辑规则')!.trigger('click')
    await wrapper.findComponent({ name: 'VTextField' }).setValue('另一个草稿')
    expect(exitPrevented()).toBe(true)
    wrapper.unmount()
    expect(exitPrevented()).toBe(false)
  })

  it('clears the previous direction list immediately and ignores stale responses', async () => {
    const incoming = deferred<unknown>()
    const outgoing = deferred<unknown>()
    vi.mocked(api).mockImplementation(apiHandler(direction => direction === 'Outgoing' ? outgoing.promise : incoming.promise))

    const wrapper = mountWorkspace()
    await flushPromises()
    incoming.resolve(rulesFor('Incoming'))
    await flushPromises()
    expect(wrapper.text()).toContain('收信归档')
    expect(rowButton(wrapper, '删除规则')).toBeTruthy()

    routeParams.section = 'outgoing'
    await flushPromises()
    // 新方向尚未返回：旧方向的记录已清空，不能继续操作。
    expect(wrapper.text()).not.toContain('收信归档')
    expect(wrapper.findAll('.qa-pill').some(pill => pill.text() === '编辑中')).toBe(false)

    outgoing.resolve(rulesFor('Outgoing'))
    await flushPromises()
    expect(wrapper.text()).toContain('发信转发')

    // 旧请求在新列表就绪后才返回，不得覆盖新方向数据。
    incoming.resolve(rulesFor('Incoming'))
    await flushPromises()
    expect(wrapper.text()).toContain('发信转发')
    expect(wrapper.text()).not.toContain('收信归档')
  })

  it('does not allow an out-of-order response to replace the newest direction', async () => {
    const incoming = deferred<unknown>()
    vi.mocked(api).mockImplementation(apiHandler(direction => direction === 'Outgoing' ? rulesFor('Outgoing') : incoming.promise))

    const wrapper = mountWorkspace()
    await flushPromises()
    routeParams.section = 'outgoing'
    await flushPromises()
    expect(wrapper.text()).toContain('发信转发')

    incoming.resolve(rulesFor('Incoming'))
    await flushPromises()
    expect(wrapper.text()).toContain('发信转发')
  })

  it('tracks unsaved changes and confirms before leaving the editor', async () => {
    const confirm = vi.fn(() => false)
    vi.stubGlobal('confirm', confirm)
    const wrapper = mountWorkspace()
    await flushPromises()

    await rowButton(wrapper, '编辑规则')!.trigger('click')
    await flushPromises()
    const saveButton = () => wrapper.findAll('button').find(button => button.text().trim() === '保存规则')!
    expect(saveButton().attributes('disabled')).toBeDefined()

    await wrapper.findComponent({ name: 'VTextField' }).setValue('新的规则名称')
    await flushPromises()
    expect(saveButton().attributes('disabled')).toBeUndefined()
    expect(wrapper.text()).toContain('未保存')

    // 取消离开：守卫必须阻止导航并保留草稿。
    expect(leaveGuards).toHaveLength(1)
    expect(leaveGuards[0]!()).toBe(false)
    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('未保存'))
    expect(wrapper.findAllComponents(TextField)[0]!.props('modelValue')).toBe('新的规则名称')

    confirm.mockReturnValue(true)
    expect(leaveGuards[0]!()).toBe(true)
  })

  it('locks the editor and navigation while a save is in flight', async () => {
    const saving = deferred<unknown>()
    const wrapper = mountWorkspace()
    await flushPromises()
    vi.mocked(api).mockImplementation(async (path: string, options?: { method?: string }) => {
      if (options?.method === 'PUT') return saving.promise as never
      return apiHandler(() => rulesFor('Incoming'))(path, options)
    })

    vi.stubGlobal('confirm', vi.fn(() => true))
    await rowButton(wrapper, '编辑规则')!.trigger('click')
    await flushPromises()
    await wrapper.findComponent({ name: 'VTextField' }).setValue('保存中的规则')
    await flushPromises()

    const saveButton = wrapper.findAll('button').find(button => button.text().trim() === '保存规则')!
    await saveButton.trigger('click')
    await flushPromises()

    expect(wrapper.findAll('button').find(button => button.text().includes('返回规则列表'))!.attributes('disabled')).toBeDefined()
    expect(updateGuards.every(guard => guard({ params: { section: 'outgoing' } }, { params: { section: 'incoming' } }) === false)).toBe(true)
    expect(leaveGuards[0]!()).toBe(false)

    saving.resolve({})
    await flushPromises()
    expect(wrapper.findAll('button').some(button => button.text().trim() === '保存规则')).toBe(false)
  })
})
