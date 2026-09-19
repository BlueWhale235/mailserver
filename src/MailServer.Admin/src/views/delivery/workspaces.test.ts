import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent } from 'vue'
import { flushPromises, shallowMount } from '@vue/test-utils'
import QueueWorkspace from './QueueWorkspace.vue'
import TlsReportsWorkspace from './TlsReportsWorkspace.vue'
import { api } from '../../lib/api'

vi.mock('../../lib/api', () => ({ api: vi.fn() }))
vi.mock('../../composables/usePermission', () => ({ usePermission: () => ({ can: () => true }) }))

const Input = defineComponent({
  name: 'SearchInput',
  props: ['modelValue'], emits: ['update:modelValue'], template: '<div />',
})
const Table = defineComponent({ template: '<table><slot /></table>' })
const mounted: Array<{ unmount: () => void }> = []
const result = (totalCount: number) => ({ items: [], totalCount, page: 1, pageSize: 20 })

function create(component: typeof QueueWorkspace | typeof TlsReportsWorkspace) {
  const wrapper = shallowMount(component, {
    props: component === QueueWorkspace ? { mode: 'queue', refreshKey: 0 } : { refreshKey: 0 },
    global: { stubs: {
      VTextField: Input, VSelect: true, VTable: Table,
      VBtn: true, VAlert: true, VSpacer: true, VCheckboxBtn: true,
      VProgressLinear: true, VIcon: true, VTooltip: true, VPagination: true,
      VCardTitle: true, VCardText: true, VCardActions: true, VCard: true, VDialog: true,
    } },
  })
  mounted.push(wrapper)
  return wrapper
}

beforeEach(() => { vi.useFakeTimers(); vi.mocked(api).mockReset(); vi.mocked(api).mockResolvedValue(result(0)) })
afterEach(() => { mounted.splice(0).forEach(wrapper => wrapper.unmount()); vi.useRealTimers() })

describe.each([
  ['queue', QueueWorkspace, '/queues'],
  ['TLS reports', TlsReportsWorkspace, '/delivery/tls-reports'],
] as const)('%s list safeguards', (_, component, endpoint) => {
  it('accepts null from the clearable input and removes the search parameter', async () => {
    const wrapper = create(component)
    await flushPromises()
    const input = wrapper.findAllComponents(Input)[0]!
    input.vm.$emit('update:modelValue', 'gmail.com')
    await wrapper.vm.$nextTick()
    await vi.advanceTimersByTimeAsync(350)
    expect(vi.mocked(api).mock.lastCall?.[0]).toContain('search=gmail.com')
    input.vm.$emit('update:modelValue', null)
    await wrapper.vm.$nextTick()
    await vi.advanceTimersByTimeAsync(350)
    expect(vi.mocked(api).mock.lastCall?.[0]).toContain(endpoint)
    expect(vi.mocked(api).mock.lastCall?.[0]).not.toContain('search=')
  })

  it('does not allow an older response to replace refreshed results', async () => {
    let resolveOld!: (value: unknown) => void
    vi.mocked(api).mockImplementationOnce(() => new Promise(resolve => { resolveOld = resolve }))
    const wrapper = create(component)
    vi.mocked(api).mockResolvedValueOnce(result(7))
    await wrapper.setProps({ refreshKey: 1 })
    await flushPromises()
    expect(wrapper.text()).toContain('共 7')
    resolveOld(result(99))
    await flushPromises()
    expect(wrapper.text()).toContain('共 7')
    expect(wrapper.text()).not.toContain('共 99')
  })

  it('cancels a pending search when leaving the workspace', async () => {
    const wrapper = create(component)
    await flushPromises()
    wrapper.findAllComponents(Input)[0]!.vm.$emit('update:modelValue', 'pending')
    await wrapper.vm.$nextTick()
    const count = vi.mocked(api).mock.calls.length
    wrapper.unmount()
    await vi.advanceTimersByTimeAsync(500)
    expect(api).toHaveBeenCalledTimes(count)
  })
})

it('clears queue-specific filters when switching to history', async () => {
  const wrapper = create(QueueWorkspace)
  await flushPromises()
  wrapper.findAllComponents(Input)[0]!.vm.$emit('update:modelValue', 'blocked.invalid')
  await wrapper.vm.$nextTick()
  await vi.advanceTimersByTimeAsync(350)
  await wrapper.setProps({ mode: 'history' })
  await vi.advanceTimersByTimeAsync(350)
  expect(vi.mocked(api).mock.lastCall?.[0]).toContain('scope=History')
  expect(vi.mocked(api).mock.lastCall?.[0]).not.toContain('search=')
})
