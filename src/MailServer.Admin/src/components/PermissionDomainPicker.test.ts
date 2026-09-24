import { beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import PermissionDomainPicker from './PermissionDomainPicker.vue'
import { api } from '../lib/api'

vi.mock('../lib/api', () => ({ api: vi.fn() }))

const domains = Array.from({ length: 125 }, (_, index) => ({
  id: `00000000-0000-0000-0000-${String(index + 1).padStart(12, '0')}`,
  name: `domain-${String(index + 1).padStart(3, '0')}.example`,
}))
const Button = defineComponent({
  name: 'VBtn', props: ['disabled'], emits: ['click'],
  template: '<button :disabled="disabled" @click="$emit(`click`)"><slot /></button>',
})
const TextField = defineComponent({
  name: 'VTextField', props: ['modelValue', 'label'], emits: ['update:modelValue'],
  template: '<label>{{ label }}<input :value="modelValue" @input="$emit(`update:modelValue`, $event.target.value)" /></label>',
})
const Checkbox = defineComponent({
  name: 'VCheckbox', props: ['modelValue', 'label', 'disabled'], emits: ['update:modelValue'],
  template: '<button :disabled="disabled" @click="$emit(`update:modelValue`, !modelValue)">{{ label }} {{ modelValue ? `已选` : `` }}</button>',
})
const Chip = defineComponent({
  name: 'VChip', emits: ['click:close'],
  template: '<span><slot /><button @click="$emit(`click:close`)">移除</button></span>',
})

function mountPicker(modelValue: string[] = []) {
  return mount(PermissionDomainPicker, {
    props: { modelValue },
    global: { stubs: { VBtn: Button, VTextField: TextField, VCheckbox: Checkbox, VChip: Chip, VProgressLinear: true } },
  })
}

beforeEach(() => {
  vi.mocked(api).mockReset()
  vi.mocked(api).mockImplementation(async path => {
    const url = new URL(String(path), 'http://localhost')
    if (url.pathname === '/permissions/domains/resolve') {
      const ids = new Set((url.searchParams.get('ids') ?? '').split(','))
      return domains.filter(domain => ids.has(domain.id)) as never
    }
    if (url.pathname === '/permissions/domains') {
      const search = url.searchParams.get('search')?.toLowerCase()
      const filtered = domains.filter(domain => !search || domain.name.includes(search))
      const page = Number(url.searchParams.get('page') ?? '1')
      return { items: filtered.slice((page - 1) * 20, page * 20), totalCount: filtered.length } as never
    }
    throw new Error(`unexpected request ${path}`)
  })
})

describe('permission domain picker', () => {
  it('loads later pages and searches domains beyond the initial 100', async () => {
    const wrapper = mountPicker(['*'])
    await flushPromises()
    expect(wrapper.text()).toContain('共 125 个可选域')
    await wrapper.findAll('button').find(button => button.text() === '下一页')!.trigger('click')
    await flushPromises()
    expect(api).toHaveBeenCalledWith('/permissions/domains?page=2&pageSize=20')
    expect(wrapper.text()).toContain('domain-021.example')

    await wrapper.find('input').setValue('domain-125')
    await wrapper.findAll('button').find(button => button.text() === '搜索')!.trigger('click')
    await flushPromises()
    expect(api).toHaveBeenCalledWith('/permissions/domains?page=1&pageSize=20&search=domain-125')
    await wrapper.findAll('button').find(button => button.text().includes('domain-125.example'))!.trigger('click')
    expect(wrapper.emitted('update:modelValue')?.at(-1)?.[0]).toEqual([domains[124].id])
    wrapper.unmount()
  })

  it('resolves selected names that are not on the current page', async () => {
    const wrapper = mountPicker([domains[124].id])
    await flushPromises()
    expect(api).toHaveBeenCalledWith(`/permissions/domains/resolve?ids=${domains[124].id}`)
    expect(wrapper.text()).toContain('domain-125.example')
    wrapper.unmount()
  })

  it('shows a failed request without claiming that no domains exist', async () => {
    vi.mocked(api).mockRejectedValue(new Error('网络连接失败'))
    const wrapper = mountPicker()
    await flushPromises()
    expect(wrapper.text()).toContain('网络连接失败')
    expect(wrapper.text()).not.toContain('没有可选择的域')
    wrapper.unmount()
  })
})
