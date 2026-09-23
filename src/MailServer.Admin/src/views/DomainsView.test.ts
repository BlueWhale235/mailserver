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
const Button = defineComponent({
  name: 'VBtn', props: ['disabled', 'loading'], emits: ['click'],
  template: '<button :disabled="disabled || loading" @click="$emit(`click`)"><slot /></button>',
})
const Checkbox = defineComponent({
  name: 'VCheckboxBtn', props: ['modelValue', 'disabled', 'ariaLabel'], emits: ['update:modelValue'],
  template: '<input type="checkbox" :aria-label="ariaLabel" :checked="modelValue" :disabled="disabled" @change="$emit(`update:modelValue`, $event.target.checked)" />',
})
const Dialog = defineComponent({
  name: 'VDialog', props: ['modelValue'],
  template: '<div v-if="modelValue" class="qa-dialog"><slot /></div>',
})

const paged = (items: unknown[]) => ({ items, totalCount: items.length, page: 1, pageSize: 20 })

const mounted: Array<{ unmount: () => void }> = []

function mountView() {
  const wrapper = shallowMount(DomainsView, {
    global: {
      stubs: {
        PageHeader: SlotWrapper, StatusPill: true, VCard: SlotWrapper, VTable: SlotWrapper,
        VTextField: TextField, VSelect: SlotWrapper, VBtn: Button, VCheckboxBtn: Checkbox,
        VDialog: Dialog, VAlert: SlotWrapper, VSpacer: true, VIcon: true,
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

  it('ignores a stale search result and clears selections as soon as the query changes', async () => {
    let resolveOld!: (value: unknown) => void
    const oldResult = new Promise(resolve => { resolveOld = resolve })
    vi.mocked(api).mockImplementation(async path => {
      const url = String(path)
      if (url.includes('search=old')) return oldResult as never
      if (url.includes('search=new')) return paged([{ id: 'new', name: 'new.example', isActive: true, dnsStatus: 'Healthy', accountCount: 1, usedBytes: 0, quotaBytes: 1 }]) as never
      return paged([{ id: 'initial', name: 'initial.example', isActive: true, dnsStatus: 'Healthy', accountCount: 1, usedBytes: 0, quotaBytes: 1 }]) as never
    })
    const wrapper = mountView()
    await flushPromises()
    await wrapper.find('input[aria-label="选择 initial.example"]').setValue(true)
    expect(wrapper.text()).toContain('已选择 1 个')

    const search = wrapper.find('input:not([type="checkbox"])')
    await search.setValue('old')
    expect(wrapper.text()).not.toContain('已选择 1 个')
    await vi.advanceTimersByTimeAsync(400)
    await search.setValue('new')
    await vi.advanceTimersByTimeAsync(400)
    await flushPromises()
    expect(wrapper.text()).toContain('new.example')

    resolveOld(paged([{ id: 'old', name: 'old.example', isActive: true, dnsStatus: 'Healthy', accountCount: 1, usedBytes: 0, quotaBytes: 1 }]))
    await flushPromises()
    expect(wrapper.text()).toContain('new.example')
    expect(wrapper.text()).not.toContain('old.example')
  })

  it('shows and freezes the exact batch deletion targets', async () => {
    const domains = [
      { id: 'one', name: 'one.example', isActive: true, dnsStatus: 'Healthy', accountCount: 1, usedBytes: 0, quotaBytes: 1 },
      { id: 'two', name: 'two.example', isActive: false, dnsStatus: 'Unhealthy', accountCount: 0, usedBytes: 0, quotaBytes: 1 },
    ]
    vi.mocked(api).mockResolvedValue(paged(domains) as never)
    const wrapper = mountView()
    await flushPromises()
    await wrapper.find('input[aria-label="选择 one.example"]').setValue(true)
    await wrapper.find('input[aria-label="选择 two.example"]').setValue(true)
    await wrapper.findAll('button').find(button => button.text().trim() === '删除所选')!.trigger('click')
    await flushPromises()
    const dialog = wrapper.find('.qa-dialog')
    expect(dialog.text()).toContain('one.example')
    expect(dialog.text()).toContain('two.example')
    expect(dialog.text()).toContain('即将删除 2 个邮件域')
    expect(dialog.text()).toContain('1 个仍在运行')
    expect(dialog.text()).toContain('运行中，不能删除')
    expect(dialog.find('input:not([type="checkbox"])').exists()).toBe(false)
    expect(dialog.findAll('button').find(button => button.text().trim() === '确认删除')!.attributes('disabled')).toBeDefined()
  })
})
