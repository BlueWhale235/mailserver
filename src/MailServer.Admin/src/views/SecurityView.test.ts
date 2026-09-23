import { afterEach, describe, expect, it, vi } from 'vitest'
import { defineComponent } from 'vue'
import { flushPromises, shallowMount } from '@vue/test-utils'
import SecurityView from './SecurityView.vue'
import { api } from '../lib/api'

vi.mock('../lib/api', () => ({ api: vi.fn(), jsonBody: (value: unknown) => JSON.stringify(value) }))
vi.mock('../stores/auth', () => ({
  useAuthStore: () => ({
    permissions: { fullAccess: true, allDomainPermissions: [], domainPermissions: {} },
    can: () => true,
  }),
}))
vi.mock('vue-router', () => ({ onBeforeRouteLeave: vi.fn() }))

const SlotWrapper = defineComponent({ name: 'SlotWrapper', template: '<div><slot /></div>' })
const TextField = defineComponent({
  name: 'VTextField', props: ['modelValue', 'label'], emits: ['update:modelValue'],
  template: '<label>{{ label }}<input :value="modelValue ?? ``" @input="$emit(`update:modelValue`, $event.target.value)" /></label>',
})
const Pagination = defineComponent({
  name: 'VPagination', props: ['modelValue', 'length'], emits: ['update:modelValue'],
  template: '<button class="qa-page-2" @click="$emit(`update:modelValue`, 2)">page 2</button>',
})
const Button = defineComponent({
  name: 'VBtn', props: ['disabled', 'loading'], emits: ['click'],
  template: '<button :disabled="disabled || loading" @click="$emit(`click`)"><slot /></button>',
})
const Dialog = defineComponent({
  name: 'VDialog', props: ['modelValue'],
  template: '<div v-if="modelValue" class="qa-dialog"><slot /></div>',
})

function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>(done => { resolve = done })
  return { promise, resolve }
}

function mountView() {
  return shallowMount(SecurityView, {
    global: { stubs: {
      PageHeader: SlotWrapper, VAlert: SlotWrapper, VProgressLinear: true, VTable: SlotWrapper, VForm: SlotWrapper,
      VCard: SlotWrapper, VCardText: SlotWrapper, VCardActions: SlotWrapper, VDialog: Dialog, VBtn: Button,
      VTextField: TextField, VTextarea: true, VSelect: true, VCheckbox: true, VSwitch: true,
      VPagination: Pagination,
    } },
  })
}

afterEach(() => {
  vi.useRealTimers()
  vi.restoreAllMocks()
})

describe('firewall server-side list', () => {
  it('deletes with a direct confirmation button and no repeated CIDR input', async () => {
    let deleted = false
    vi.mocked(api).mockImplementation(async (path, init) => {
      if (init?.method === 'DELETE') { deleted = true; return undefined as never }
      return {
        items: deleted ? [] : [{ id: 'rule-1', cidr: '192.0.2.0/24', note: 'test', isEnabled: true }],
        page: 1, pageSize: 20, totalCount: deleted ? 0 : 1,
      } as never
    })
    const wrapper = mountView()
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text().trim() === '删除')!.trigger('click')
    await flushPromises()
    const dialog = wrapper.find('.qa-dialog')
    expect(dialog.text()).toContain('192.0.2.0/24')
    expect(dialog.find('input').exists()).toBe(false)
    const confirm = dialog.findAll('button').find(button => button.text().trim() === '确认删除')!
    expect(confirm.attributes('disabled')).toBeUndefined()
    await confirm.trigger('click')
    await flushPromises()
    expect(vi.mocked(api).mock.calls.some(call => call[0] === '/firewall/rules/rule-1' && call[1]?.method === 'DELETE')).toBe(true)
    expect(wrapper.text()).toContain('规则已删除')
    wrapper.unmount()
  })

  it('requests a new page and ignores its stale result after a debounced search', async () => {
    vi.useFakeTimers()
    const oldPage = deferred<unknown>()
    vi.mocked(api).mockImplementation(async path => {
      const url = String(path)
      if (url.includes('query=needle')) {
        return { items: [{ id: 'match', cidr: '203.0.113.9/32', note: 'needle', isEnabled: true }], page: 1, pageSize: 20, totalCount: 1 } as never
      }
      if (url.includes('page=2')) return oldPage.promise as never
      return { items: [{ id: 'first', cidr: '192.0.2.1/32', note: 'first', isEnabled: true }], page: 1, pageSize: 20, totalCount: 45 } as never
    })

    const wrapper = mountView()
    await flushPromises()
    expect(wrapper.text()).toContain('192.0.2.1/32')
    await wrapper.find('.qa-page-2').trigger('click')
    await flushPromises()
    await wrapper.findComponent({ name: 'VTextField' }).setValue('needle')
    await vi.advanceTimersByTimeAsync(300)
    await flushPromises()
    expect(wrapper.text()).toContain('203.0.113.9/32')
    oldPage.resolve({ items: [{ id: 'old', cidr: '198.51.100.2/32', note: 'old page', isEnabled: true }], page: 2, pageSize: 20, totalCount: 45 })
    await flushPromises()
    expect(wrapper.text()).toContain('203.0.113.9/32')
    expect(wrapper.text()).not.toContain('198.51.100.2/32')
    expect(vi.mocked(api).mock.calls.some(call => String(call[0]).includes('query=needle'))).toBe(true)
    wrapper.unmount()
  })
})
