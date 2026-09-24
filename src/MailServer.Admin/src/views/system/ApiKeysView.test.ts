import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent } from 'vue'
import { flushPromises, shallowMount } from '@vue/test-utils'
import ApiKeysView from './ApiKeysView.vue'
import { api } from '../../lib/api'

vi.mock('../../lib/api', () => ({ api: vi.fn(), jsonBody: (value: unknown) => JSON.stringify(value) }))
vi.mock('../../composables/usePermission', () => ({ usePermission: () => ({ can: () => true }) }))

const SlotWrapper = defineComponent({ name: 'SlotWrapper', template: '<div><slot /></div>' })
const Dialog = defineComponent({
  name: 'VDialog', props: ['modelValue'],
  template: '<div v-if="modelValue" class="qa-dialog"><slot /></div>',
})
const Button = defineComponent({
  name: 'VBtn', props: ['disabled', 'loading', 'ariaLabel'], emits: ['click'],
  template: '<button :aria-label="ariaLabel" :disabled="disabled || loading" @click="$emit(`click`)"><slot /></button>',
})
const TextField = defineComponent({
  name: 'VTextField', props: ['modelValue', 'label'], emits: ['update:modelValue'],
  template: '<label>{{ label }}<input :value="modelValue ?? ``" @input="$emit(`update:modelValue`, $event.target.value)" /></label>',
})
const Textarea = defineComponent({
  name: 'VTextarea', props: ['modelValue', 'label'],
  template: '<label>{{ label }}<textarea :value="modelValue" readonly /></label>',
})
const Checkbox = defineComponent({
  name: 'VCheckbox', props: ['modelValue', 'label'], emits: ['update:modelValue'],
  template: '<label><input type="checkbox" :checked="modelValue" @change="$emit(`update:modelValue`, $event.target.checked)" />{{ label }}</label>',
})
const Pagination = defineComponent({
  name: 'VPagination', props: ['modelValue', 'length'], emits: ['update:modelValue'],
  template: '<button v-if="length > 1" @click="$emit(`update:modelValue`, 2)">下一页</button>',
})

function mountView() {
  return shallowMount(ApiKeysView, {
    global: {
      stubs: {
        RouterLink: SlotWrapper,
        PageHeader: SlotWrapper,
        PermissionGrantEditor: SlotWrapper,
        StatusPill: SlotWrapper,
        VIcon: true,
        VSpacer: true,
        VAlert: SlotWrapper,
        VProgressLinear: true,
        VTable: SlotWrapper,
        VCard: SlotWrapper,
        VCardTitle: SlotWrapper,
        VCardText: SlotWrapper,
        VCardActions: SlotWrapper,
        VDialog: Dialog,
        VBtn: Button,
        VTextField: TextField,
        VSelect: TextField,
        VPagination: Pagination,
        VTextarea: Textarea,
        VCheckbox: Checkbox,
      },
    },
  })
}

beforeEach(() => {
  vi.mocked(api).mockReset()
  vi.mocked(api).mockImplementation(async (path, init) => {
    if (path === '/api-keys' && init?.method === 'POST') return { apiKey: 'msk_test_secret_value' } as never
    if (String(path).startsWith('/api-keys?')) return { items: [], page: 1, pageSize: 20, totalCount: 0 } as never
    if (path === '/permissions/catalog') return [] as never
    if (String(path).startsWith('/domains/search')) return { items: [], page: 1, pageSize: 100, totalCount: 0 } as never
    throw new Error(`unexpected request ${path}`)
  })
})

afterEach(() => {
  vi.restoreAllMocks()
})

describe('API Key one-time secret', () => {
  it('shows a load failure without claiming that no keys exist', async () => {
    vi.mocked(api).mockRejectedValue(new Error('网络连接失败'))
    const wrapper = mountView()
    await flushPromises()
    expect(wrapper.text()).toContain('API Key 列表加载失败，请点击刷新重试')
    expect(wrapper.text()).not.toContain('还没有创建 API Key')
    expect(wrapper.text()).not.toContain('第 1 / 1 页')
    wrapper.unmount()
  })

  it('requests server-side filters and pages', async () => {
    vi.mocked(api).mockImplementation(async path => {
      if (String(path).startsWith('/api-keys?')) return {
        items: [], page: 1, pageSize: 20, totalCount: 21,
      } as never
      throw new Error(`unexpected request ${path}`)
    })
    const wrapper = mountView()
    await flushPromises()
    wrapper.findAllComponents(TextField).find(field => field.props('label') === '搜索名称、前缀或创建人')!
      .vm.$emit('update:modelValue', '  AGENT  ')
    await wrapper.vm.$nextTick()
    await wrapper.findAll('button').find(button => button.text() === '筛选')!.trigger('click')
    await flushPromises()
    expect(api).toHaveBeenCalledWith('/api-keys?page=1&pageSize=20&search=AGENT')

    await wrapper.findAll('button').find(button => button.text() === '下一页')!.trigger('click')
    await flushPromises()
    expect(api).toHaveBeenCalledWith('/api-keys?page=2&pageSize=20&search=AGENT')
    wrapper.unmount()
  })

  it('reloads the preceding page when the final page becomes empty', async () => {
    let total = 21
    vi.mocked(api).mockImplementation(async path => {
      if (String(path).startsWith('/api-keys?')) {
        const requestedPage = Number(new URL(String(path), 'http://localhost').searchParams.get('page'))
        return { items: [], page: requestedPage, pageSize: 20, totalCount: total } as never
      }
      throw new Error(`unexpected request ${path}`)
    })
    const wrapper = mountView()
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '下一页')!.trigger('click')
    await flushPromises()
    total = 20
    await wrapper.find('button[aria-label="刷新 API Key 列表"]').trigger('click')
    await flushPromises()
    expect(api).toHaveBeenLastCalledWith('/api-keys?page=1&pageSize=20')
    expect(wrapper.text()).toContain('第 1 / 1 页 · 共 20 个')
    wrapper.unmount()
  })

  it('confirms before discarding a dirty creation draft', async () => {
    const wrapper = mountView()
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '新建 API Key')!.trigger('click')
    await flushPromises()
    wrapper.findAllComponents(TextField).find(field => field.props('label') === '名称')!.vm.$emit('update:modelValue', 'unfinished-agent')
    await wrapper.vm.$nextTick()

    await wrapper.findAll('.qa-dialog').find(dialog => dialog.text().includes('新建 API Key'))!
      .findAll('button').find(button => button.text() === '取消')!.trigger('click')
    await wrapper.vm.$nextTick()
    const discard = wrapper.findAll('.qa-dialog').find(dialog => dialog.text().includes('放弃未保存修改'))!
    expect(discard.text()).toContain('初始授权尚未提交')
    await discard.findAll('button').find(button => button.text() === '继续编辑')!.trigger('click')
    await wrapper.vm.$nextTick()
    expect(wrapper.findAll('.qa-dialog').some(dialog => dialog.text().includes('新建 API Key'))).toBe(true)
    wrapper.unmount()
  })

  it('does not allow saving an unchanged authorization set', async () => {
    const key = { id: 'key-1', name: 'metrics', prefix: 'msk_test', createdBy: 'admin', createdAt: '2026-01-01T00:00:00Z' }
    vi.mocked(api).mockImplementation(async path => {
      if (String(path).startsWith('/api-keys?')) return { items: [key], page: 1, pageSize: 20, totalCount: 1 } as never
      if (path === '/api-keys/key-1/grants') return [{ permissionCode: 'dashboard.read', resourceType: '', resourceKey: '' }] as never
      if (path === '/permissions/catalog') return [] as never
      if (String(path).startsWith('/domains/search')) return { items: [], page: 1, pageSize: 100, totalCount: 0 } as never
      throw new Error(`unexpected request ${path}`)
    })
    const wrapper = mountView()
    await flushPromises()
    await wrapper.find('button[aria-label="编辑 metrics 的授权"]').trigger('click')
    await flushPromises()
    const dialog = wrapper.findAll('.qa-dialog').find(item => item.text().includes('编辑授权 · metrics'))!
    expect(dialog.findAll('button').find(button => button.text() === '保存授权')!.attributes('disabled')).toBeDefined()
    wrapper.unmount()
  })

  it('keeps the dialog open until the secret is copied, then clears it on close', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined)
    Object.defineProperty(globalThis.navigator, 'clipboard', { configurable: true, value: { writeText } })
    const wrapper = mountView()
    await flushPromises()

    await wrapper.findAll('button').find(button => button.text() === '新建 API Key')!.trigger('click')
    await flushPromises()
    wrapper.findAllComponents(TextField).find(field => field.props('label') === '名称')!.vm.$emit('update:modelValue', 'metrics-exporter')
    await wrapper.vm.$nextTick()
    await wrapper.findAll('button').find(button => button.text() === '创建 API Key')!.trigger('click')
    await flushPromises()

    const secretDialog = wrapper.findAll('.qa-dialog').find(dialog => dialog.text().includes('API Key 已生成'))!
    const closeButton = secretDialog.findAll('button').find(button => button.text() === '我已安全保存并关闭')!
    expect(closeButton.attributes('disabled')).toBeDefined()
    expect(secretDialog.find('textarea').element.value).toBe('msk_test_secret_value')

    await secretDialog.findAll('button').find(button => button.text() === '复制密钥')!.trigger('click')
    await flushPromises()
    expect(writeText).toHaveBeenCalledWith('msk_test_secret_value')
    expect(secretDialog.text()).toContain('密钥已写入剪贴板')
    expect(closeButton.attributes('disabled')).toBeUndefined()

    await closeButton.trigger('click')
    await wrapper.vm.$nextTick()
    expect(wrapper.text()).not.toContain('msk_test_secret_value')
    wrapper.unmount()
  })

  it('allows an explicit safe-storage acknowledgement when clipboard access is unavailable', async () => {
    Object.defineProperty(globalThis.navigator, 'clipboard', { configurable: true, value: undefined })
    const wrapper = mountView()
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '新建 API Key')!.trigger('click')
    await flushPromises()
    wrapper.findAllComponents(TextField).find(field => field.props('label') === '名称')!.vm.$emit('update:modelValue', 'backup-agent')
    await wrapper.vm.$nextTick()
    await wrapper.findAll('button').find(button => button.text() === '创建 API Key')!.trigger('click')
    await flushPromises()

    const secretDialog = wrapper.findAll('.qa-dialog').find(dialog => dialog.text().includes('API Key 已生成'))!
    await secretDialog.findAll('button').find(button => button.text() === '复制密钥')!.trigger('click')
    await flushPromises()
    expect(secretDialog.text()).toContain('请手动复制上方密钥')
    await secretDialog.find('input[type="checkbox"]').setValue(true)
    expect(secretDialog.findAll('button').find(button => button.text() === '我已安全保存并关闭')!.attributes('disabled')).toBeUndefined()
    wrapper.unmount()
  })

  it('protects browser refresh while an unacknowledged secret is visible', async () => {
    const wrapper = mountView()
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '新建 API Key')!.trigger('click')
    await flushPromises()
    wrapper.findAllComponents(TextField).find(field => field.props('label') === '名称')!.vm.$emit('update:modelValue', 'ci-runner')
    await wrapper.vm.$nextTick()
    await wrapper.findAll('button').find(button => button.text() === '创建 API Key')!.trigger('click')
    await flushPromises()

    const event = new Event('beforeunload', { cancelable: true })
    globalThis.dispatchEvent(event)
    expect(event.defaultPrevented).toBe(true)
    wrapper.unmount()
  })
})
