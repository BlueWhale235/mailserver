import { beforeEach, afterEach, describe, expect, it, vi } from 'vitest'
import { defineComponent } from 'vue'
import { flushPromises, shallowMount } from '@vue/test-utils'
import DomainsView from './DomainsView.vue'
import { api } from '../lib/api'

vi.mock('../lib/api', () => ({ api: vi.fn(), jsonBody: (value: unknown) => JSON.stringify(value) }))
vi.mock('../composables/usePermission', () => ({ usePermission: () => ({ can: () => true }) }))

const SlotWrapper = defineComponent({ name: 'SlotWrapper', props: { title: { type: String, default: '' } }, template: '<div><slot /></div>' })
const TextField = defineComponent({
  name: 'VTextField',
  props: { modelValue: { type: [String, Array], default: '' }, placeholder: { type: String, default: '' }, label: { type: String, default: '' } },
  emits: ['update:modelValue'],
  template: '<label><span>{{ placeholder }}</span><input :value="modelValue" @input="$emit(\'update:modelValue\', ($event.target).value === \'\' ? null : ($event.target).value)" /></label>',
})

const paged = (items: unknown[]) => ({ items, totalCount: items.length, page: 1, pageSize: 20 })

const mounted: Array<{ unmount: () => void }> = []

function mountView() {
  const wrapper = shallowMount(DomainsView, {
    global: {
      stubs: {
        PageHeader: SlotWrapper, StatusPill: true, VCard: SlotWrapper, VTable: SlotWrapper,
        VTextField: TextField, VSelect: SlotWrapper, VBtn: SlotWrapper, VCheckboxBtn: true,
        VDialog: SlotWrapper, VAlert: SlotWrapper, VSpacer: true, VIcon: true,
        VPagination: true, VProgressLinear: true, VTooltip: SlotWrapper,
      },
    },
  })
  mounted.push(wrapper)
  return wrapper
}

const searchCalls = () => vi.mocked(api).mock.calls.map(c => String(c[0])).filter(u => u.includes('/domains/search'))

beforeEach(() => {
  vi.useFakeTimers()
  vi.mocked(api).mockReset()
  vi.mocked(api).mockResolvedValue(paged([]) as never)
})
afterEach(() => { mounted.splice(0).forEach(w => w.unmount()); vi.useRealTimers() })

describe('domain list search clearing', () => {
  it('survives a null search value from the clearable input without throwing', async () => {
    const wrapper = mountView()
    await flushPromises()
    const input = wrapper.find('input')
    expect(input.exists()).toBe(true)

    await input.setValue('north')
    await vi.advanceTimersByTimeAsync(400)
    expect(searchCalls().length).toBeGreaterThan(0)

    // Vuetify's clearable control emits null, not an empty string
    const emitted = input.element as HTMLInputElement
    emitted.value = ''
    await input.trigger('input')
    await vi.advanceTimersByTimeAsync(400)
    await flushPromises()

    // clearing must trigger a fresh unfiltered load rather than an early-return error
    const calls = searchCalls()
    expect(calls.at(-1)).not.toContain('search=')
    expect(calls.length).toBeGreaterThan(1)
  })
})
