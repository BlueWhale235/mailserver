import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, nextTick } from 'vue'
import { flushPromises, shallowMount } from '@vue/test-utils'
import SystemSettingsView from './SystemSettingsView.vue'
import { api } from '../../lib/api'

let leaveGuard: (() => boolean | Promise<boolean>) | undefined

vi.mock('../../lib/api', () => ({ api: vi.fn(), jsonBody: (value: unknown) => JSON.stringify(value) }))
vi.mock('../../composables/usePermission', () => ({ usePermission: () => ({ can: () => true }) }))
vi.mock('vue-router', () => ({
  onBeforeRouteLeave: (guard: () => boolean | Promise<boolean>) => { leaveGuard = guard },
}))

const SlotWrapper = defineComponent({
  name: 'SlotWrapper',
  props: ['title', 'type'],
  template: '<div><span v-if="title">{{ title }}</span><slot /></div>',
})
const Dialog = defineComponent({
  name: 'VDialog',
  props: ['modelValue'],
  template: '<div v-if="modelValue" class="qa-dialog"><slot /></div>',
})
const Button = defineComponent({
  name: 'VBtn',
  props: ['disabled', 'loading', 'type'],
  emits: ['click'],
  template: '<button :type="type || `button`" :disabled="disabled || loading" @click="$emit(`click`)"><slot /></button>',
})
const TextField = defineComponent({
  name: 'VTextField',
  props: ['modelValue', 'label'],
  emits: ['update:modelValue'],
  template: '<label>{{ label }}<input :value="modelValue" @input="$emit(`update:modelValue`, Number($event.target.value))" /></label>',
})

function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>(done => { resolve = done })
  return { promise, resolve }
}

function mountView() {
  return shallowMount(SystemSettingsView, {
    global: {
      stubs: {
        RouterLink: SlotWrapper,
        PageHeader: SlotWrapper,
        StatusPill: SlotWrapper,
        VAlert: SlotWrapper,
        VProgressLinear: true,
        VIcon: true,
        VChip: SlotWrapper,
        VSpacer: true,
        VCard: SlotWrapper,
        VCardTitle: SlotWrapper,
        VCardText: SlotWrapper,
        VCardActions: SlotWrapper,
        VDialog: Dialog,
        VBtn: Button,
        VTextField: TextField,
        VCombobox: SlotWrapper,
        VSwitch: SlotWrapper,
      },
    },
  })
}

const delivery = { batchSize: 20, leaseSeconds: 120, maximumAttempts: 5, pollIntervalSeconds: 5 }

beforeEach(() => {
  leaveGuard = undefined
  vi.mocked(api).mockReset()
  vi.mocked(api).mockImplementation(async path => {
    if (path === '/settings/delivery') return { ...delivery } as never
    if (path === '/settings/smtp') return { commandTimeoutSeconds: 300 } as never
    if (path === '/settings/mailboxes') return { defaultFolders: ['INBOX', 'Sent', 'Drafts', 'Trash', 'Junk', 'Archives'] } as never
    if (path === '/settings/certificates') return {
      defaultMailHostName: null,
      allowSingleCertificateFallback: false,
      availableMailHostNames: ['mail.example.test'],
      effectiveDefaultMailHostName: null,
    } as never
    throw new Error(`unexpected request ${path}`)
  })
})

afterEach(() => {
  vi.restoreAllMocks()
})

describe('system settings safeguards', () => {
  it('uses an in-page confirmation dialog when leaving with unsaved settings', async () => {
    const confirm = vi.spyOn(globalThis, 'confirm')
    const wrapper = mountView()
    await flushPromises()

    wrapper.findAllComponents(TextField)[0]!.vm.$emit('update:modelValue', 30)
    await nextTick()
    const result = leaveGuard!()
    await nextTick()

    expect(confirm).not.toHaveBeenCalled()
    expect(wrapper.find('.qa-dialog').text()).toContain('1 个设置分区尚未保存')
    await wrapper.find('.qa-dialog').findAll('button').find(button => button.text() === '继续编辑')!.trigger('click')
    await expect(result).resolves.toBe(false)
    wrapper.unmount()
  })

  it('locks section navigation while saving and keeps the success label tied to the saved section', async () => {
    const pendingSave = deferred<typeof delivery>()
    vi.mocked(api).mockImplementation(async (path, init) => {
      if (path === '/settings/delivery' && init?.method === 'PUT') return pendingSave.promise as never
      if (path === '/settings/delivery') return { ...delivery } as never
      if (path === '/settings/smtp') return { commandTimeoutSeconds: 300 } as never
      if (path === '/settings/mailboxes') return { defaultFolders: ['INBOX', 'Sent', 'Drafts', 'Trash', 'Junk', 'Archives'] } as never
      return { defaultMailHostName: null, allowSingleCertificateFallback: false, availableMailHostNames: [], effectiveDefaultMailHostName: null } as never
    })
    const wrapper = mountView()
    await flushPromises()
    wrapper.findAllComponents(TextField)[0]!.vm.$emit('update:modelValue', 30)
    await nextTick()

    await wrapper.findAll('button').find(button => button.text() === '保存当前设置')!.trigger('click')
    await nextTick()
    const smtpButton = wrapper.findAll('.settings-nav button').find(button => button.text().includes('SMTP 会话'))!
    expect(smtpButton.attributes('disabled')).toBeDefined()
    expect(wrapper.find('.settings-nav button.active').text()).toContain('投递调度')

    pendingSave.resolve({ ...delivery, batchSize: 30 })
    await flushPromises()
    expect(wrapper.text()).toContain('投递调度已保存')
    expect(wrapper.text()).not.toContain('SMTP 会话已保存')
    wrapper.unmount()
  })

  it('protects browser refresh after a setting changes', async () => {
    const wrapper = mountView()
    await flushPromises()
    wrapper.findAllComponents(TextField)[0]!.vm.$emit('update:modelValue', 30)
    await nextTick()

    const event = new Event('beforeunload', { cancelable: true })
    globalThis.dispatchEvent(event)
    expect(event.defaultPrevented).toBe(true)
    wrapper.unmount()
  })
})
