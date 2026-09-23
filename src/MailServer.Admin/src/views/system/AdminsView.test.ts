import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent } from 'vue'
import { flushPromises, shallowMount } from '@vue/test-utils'
import AdminsView from './AdminsView.vue'
import { api } from '../../lib/api'

let leaveGuard: (() => boolean | Promise<boolean>) | undefined

vi.mock('../../lib/api', () => ({ api: vi.fn(), jsonBody: (value: unknown) => JSON.stringify(value) }))
vi.mock('../../composables/usePermission', () => ({ usePermission: () => ({ can: () => true }) }))
vi.mock('vue-router', () => ({
  onBeforeRouteLeave: (guard: () => boolean | Promise<boolean>) => { leaveGuard = guard },
}))

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
  name: 'VTextField', props: ['modelValue', 'label', 'placeholder'], emits: ['update:modelValue'],
  template: '<label>{{ label || placeholder }}<input :value="modelValue ?? ``" @input="$emit(`update:modelValue`, $event.target.value)" /></label>',
})
const PermissionEditor = defineComponent({
  name: 'PermissionGrantEditor', props: ['modelValue'], emits: ['update:modelValue'],
  template: '<button class="qa-change-grants" @click="$emit(`update:modelValue`, [...modelValue, { permissionCode: `audit.read`, resourceType: ``, resourceKey: `` }])">change grants</button>',
})
const Pagination = defineComponent({
  name: 'VPagination', props: ['modelValue'], emits: ['update:modelValue'],
  template: '<button class="qa-page-2" @click="$emit(`update:modelValue`, 2)">page 2</button>',
})

const admin = {
  id: 'admin-123456789', userName: 'operator', isEnabled: true,
  createdAt: '2026-01-01T00:00:00Z', isPrimary: false,
}
const initialGrant = { permissionCode: 'dashboard.read', resourceType: '', resourceKey: '' }

function mountView() {
  return shallowMount(AdminsView, {
    global: {
      stubs: {
        RouterLink: SlotWrapper,
        PageHeader: SlotWrapper,
        PermissionGrantEditor: PermissionEditor,
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
        VSelect: SlotWrapper,
        VSwitch: SlotWrapper,
        VPagination: Pagination,
      },
    },
  })
}

beforeEach(() => {
  leaveGuard = undefined
  vi.mocked(api).mockReset()
  vi.mocked(api).mockImplementation(async (path, init) => {
    if (String(path).startsWith('/admins?')) return { items: [admin], page: 1, pageSize: 20, totalCount: 21 } as never
    if (path === '/permissions/catalog') return [] as never
    if (String(path).startsWith('/domains/search')) return { items: [], page: 1, pageSize: 100, totalCount: 0 } as never
    if (path === '/admins/admin-123456789/grants') return [initialGrant] as never
    if (init?.method) return undefined as never
    throw new Error(`unexpected request ${path}`)
  })
})

afterEach(() => {
  vi.restoreAllMocks()
})

describe('administrator management safeguards', () => {
  it('requests the selected server page instead of paginating a local 100-row snapshot', async () => {
    const wrapper = mountView()
    await flushPromises()
    await wrapper.find('.qa-page-2').trigger('click')
    await flushPromises()
    expect(vi.mocked(api).mock.calls.some(call => String(call[0]).includes('page=2') && String(call[0]).includes('pageSize=20'))).toBe(true)
    wrapper.unmount()
  })

  it('disables an unchanged edit and confirms before discarding changed administrator details', async () => {
    const wrapper = mountView()
    await flushPromises()
    await wrapper.find('button[aria-label="编辑 operator"]').trigger('click')
    await wrapper.vm.$nextTick()
    const editDialog = wrapper.findAll('.qa-dialog').find(dialog => dialog.text().includes('编辑管理员'))!
    expect(editDialog.findAll('button').find(button => button.text() === '保存修改')!.attributes('disabled')).toBeDefined()

    await editDialog.findAll('label').find(field => field.text().includes('用户名'))!.find('input').setValue('operator-2')
    await wrapper.vm.$nextTick()
    expect(editDialog.findAll('button').find(button => button.text() === '保存修改')!.attributes('disabled')).toBeUndefined()
    await editDialog.findAll('button').find(button => button.text() === '取消')!.trigger('click')
    await wrapper.vm.$nextTick()
    expect(wrapper.findAll('.qa-dialog').some(dialog => dialog.text().includes('管理员资料或新密码尚未保存'))).toBe(true)
    wrapper.unmount()
  })

  it('disables unchanged grants and protects a modified authorization draft', async () => {
    const wrapper = mountView()
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '授权')!.trigger('click')
    await flushPromises()
    const grantsDialog = wrapper.findAll('.qa-dialog').find(dialog => dialog.text().includes('编辑授权 · operator'))!
    expect(grantsDialog.findAll('button').find(button => button.text() === '保存授权')!.attributes('disabled')).toBeDefined()
    await grantsDialog.find('.qa-change-grants').trigger('click')
    await wrapper.vm.$nextTick()
    expect(grantsDialog.findAll('button').find(button => button.text() === '保存授权')!.attributes('disabled')).toBeUndefined()
    await grantsDialog.findAll('button').find(button => button.text() === '取消')!.trigger('click')
    await wrapper.vm.$nextTick()
    expect(wrapper.findAll('.qa-dialog').some(dialog => dialog.text().includes('授权修改尚未保存'))).toBe(true)
    wrapper.unmount()
  })

  it('uses an in-page route confirmation and protects browser refresh for a dirty creation form', async () => {
    const confirm = vi.spyOn(globalThis, 'confirm')
    const wrapper = mountView()
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '新建管理员')!.trigger('click')
    await flushPromises()
    wrapper.findAllComponents(TextField).find(field => field.props('label') === '用户名')!.vm.$emit('update:modelValue', 'new-operator')
    await wrapper.vm.$nextTick()

    const unload = new Event('beforeunload', { cancelable: true })
    globalThis.dispatchEvent(unload)
    expect(unload.defaultPrevented).toBe(true)
    const result = leaveGuard!()
    await wrapper.vm.$nextTick()
    expect(confirm).not.toHaveBeenCalled()
    const discard = wrapper.findAll('.qa-dialog').find(dialog => dialog.text().includes('当前有 1 个管理员表单尚未保存'))!
    await discard.findAll('button').find(button => button.text() === '继续编辑')!.trigger('click')
    await expect(result).resolves.toBe(false)
    wrapper.unmount()
  })

  it('requires the exact administrator name before enabling deletion', async () => {
    const wrapper = mountView()
    await flushPromises()
    await wrapper.find('button[aria-label="删除 operator"]').trigger('click')
    await wrapper.vm.$nextTick()
    const dialog = wrapper.findAll('.qa-dialog').find(item => item.text().includes('删除管理员'))!
    const confirm = dialog.findAll('button').find(button => button.text() === '确认删除')!
    expect(confirm.attributes('disabled')).toBeDefined()
    await dialog.findAll('label').find(field => field.text().includes('管理员名称'))!.find('input').setValue('operator')
    await wrapper.vm.$nextTick()
    expect(confirm.attributes('disabled')).toBeUndefined()
    wrapper.unmount()
  })
})
