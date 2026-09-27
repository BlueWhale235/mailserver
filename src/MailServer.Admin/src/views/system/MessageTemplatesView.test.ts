import { beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, nextTick } from 'vue'
import { flushPromises, shallowMount } from '@vue/test-utils'
import MessageTemplatesView from './MessageTemplatesView.vue'
import { api } from '../../lib/api'

let leaveGuard: (() => boolean | Promise<boolean>) | undefined
vi.mock('../../lib/api', () => ({
  api: vi.fn(), jsonBody: (value: unknown) => JSON.stringify(value), ApiError: class extends Error {},
}))
vi.mock('../../composables/usePermission', () => ({ usePermission: () => ({ can: () => true }) }))
vi.mock('vue-router', () => ({ onBeforeRouteLeave: (guard: () => boolean | Promise<boolean>) => { leaveGuard = guard } }))

const SlotWrapper = defineComponent({ name: 'SlotWrapper', template: '<div><slot /></div>' })
const Dialog = defineComponent({ name: 'VDialog', props: ['modelValue'], template: '<div v-if="modelValue" class="qa-dialog"><slot /></div>' })
const Button = defineComponent({
  name: 'VBtn', props: ['disabled', 'loading'], emits: ['click'],
  template: '<button :disabled="disabled || loading" @click="$emit(`click`)"><slot /></button>',
})
const TextField = defineComponent({ name: 'VTextField', props: ['modelValue', 'label'], emits: ['update:modelValue'], template: '<label>{{ label }}<input :value="modelValue" @input="$emit(`update:modelValue`, $event.target.value)" /></label>' })
const Textarea = defineComponent({ name: 'VTextarea', props: ['modelValue', 'label'], emits: ['update:modelValue'], template: '<label>{{ label }}<textarea :value="modelValue" @input="$emit(`update:modelValue`, $event.target.value)" /></label>' })

const initial = {
  senderDisplayName: '投递系统', permanentFailureSubject: '失败：{recipient}',
  permanentFailureBody: '<p>{reason}</p>', retryExhaustedSubject: '重试耗尽',
  retryExhaustedBody: '<p>{attempts}</p>',
  quotaExceededSubject: '超限：{recipient}', quotaExceededBody: '<p>{reason}；请手动联系收件人</p>',
}

function mountView() {
  return shallowMount(MessageTemplatesView, {
    global: { stubs: {
      RouterLink: SlotWrapper, PageHeader: SlotWrapper, VIcon: true, VSpacer: true, VAlert: SlotWrapper,
      VProgressLinear: true, VCard: SlotWrapper, VCardTitle: SlotWrapper, VCardText: SlotWrapper,
      VCardActions: SlotWrapper, VDialog: Dialog, VBtn: Button, VTextField: TextField,
      VTextarea: Textarea, VChip: SlotWrapper,
    } },
  })
}

function button(wrapper: ReturnType<typeof mountView>, label: string) {
  return wrapper.findAll('button').find(item => item.text() === label)!
}

beforeEach(() => {
  leaveGuard = undefined
  vi.mocked(api).mockReset()
  vi.mocked(api).mockImplementation(async (path, init) => {
    if (path === '/settings/message-templates' && init?.method === 'PUT') return JSON.parse(String(init.body)) as never
    if (path === '/settings/message-templates') return { ...initial } as never
    if (path === '/settings/message-templates/defaults') return { ...initial, senderDisplayName: '默认投递系统' } as never
    if (path === '/settings/message-templates/preview') return { renderedSubject: '服务端主题', renderedHtml: '<p>服务端正文</p>' } as never
    throw new Error(`unexpected request ${path}`)
  })
})

describe('message template editor', () => {
  it('edits, previews and saves the quota template separately', async () => {
    const wrapper = mountView()
    await flushPromises()
    await wrapper.findAll('[role="tab"]').find(tab => tab.text().includes('超限退信'))!.trigger('click')
    expect(wrapper.findAllComponents(TextField)[1]!.props('modelValue')).toBe(initial.quotaExceededSubject)
    expect(wrapper.text()).toContain('552 5.2.2')
    wrapper.findAllComponents(TextField)[1]!.vm.$emit('update:modelValue', '自定义超限主题')
    await nextTick()
    await button(wrapper, '服务端预览').trigger('click')
    await flushPromises()
    const preview = vi.mocked(api).mock.calls.find(([path]) => path === '/settings/message-templates/preview')!
    expect(JSON.parse(String(preview[1]?.body))).toEqual({ subject: '自定义超限主题', body: initial.quotaExceededBody, templateKind: 'quota' })
    await button(wrapper, '保存模板').trigger('click')
    await flushPromises()
    const save = vi.mocked(api).mock.calls.find(([, init]) => init?.method === 'PUT')!
    expect(JSON.parse(String(save[1]?.body))).toEqual({ ...initial, quotaExceededSubject: '自定义超限主题' })
    expect(wrapper.text()).toContain('退信消息模板已保存')
    wrapper.unmount()
  })
  it('confirms undo and preserves the draft when cancelled', async () => {
    const wrapper = mountView()
    await flushPromises()
    wrapper.findComponent(TextField).vm.$emit('update:modelValue', '未保存草稿')
    await nextTick()
    await button(wrapper, '撤销修改').trigger('click')
    expect(wrapper.find('.qa-dialog').text()).toContain('当前草稿无法找回')
    await button(wrapper, '继续编辑').trigger('click')
    expect(wrapper.findComponent(TextField).props('modelValue')).toBe('未保存草稿')
    await button(wrapper, '撤销修改').trigger('click')
    await button(wrapper, '确认撤销').trigger('click')
    expect(wrapper.findComponent(TextField).props('modelValue')).toBe(initial.senderDisplayName)
    wrapper.unmount()
  })

  it('previews through the API and loads defaults as an unsaved draft', async () => {
    const wrapper = mountView()
    await flushPromises()
    await button(wrapper, '服务端预览').trigger('click')
    await flushPromises()
    expect(vi.mocked(api)).toHaveBeenCalledWith('/settings/message-templates/preview', expect.objectContaining({ method: 'POST' }))
    expect(wrapper.text()).toContain('已按真实退信渲染链路通过服务端验证')
    await button(wrapper, '恢复系统默认值').trigger('click')
    await button(wrapper, '载入默认值').trigger('click')
    await flushPromises()
    expect(wrapper.findComponent(TextField).props('modelValue')).toBe('默认投递系统')
    expect(button(wrapper, '保存模板').attributes('disabled')).toBeUndefined()
    wrapper.unmount()
  })

  it('uses a leave dialog and keeps a failed save draft', async () => {
    const wrapper = mountView()
    await flushPromises()
    wrapper.findComponent(TextField).vm.$emit('update:modelValue', '保留草稿')
    await nextTick()
    const leaving = leaveGuard!()
    await nextTick()
    expect(wrapper.find('.qa-dialog').text()).toContain('离开并放弃模板修改')
    await button(wrapper, '继续编辑').trigger('click')
    expect(await leaving).toBe(false)
    vi.mocked(api).mockRejectedValueOnce(new Error('保存失败'))
    await button(wrapper, '保存模板').trigger('click')
    await flushPromises()
    expect(wrapper.findComponent(TextField).props('modelValue')).toBe('保留草稿')
    expect(wrapper.text()).toContain('保存失败')
    wrapper.unmount()
  })
})
