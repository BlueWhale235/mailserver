import { afterEach, describe, expect, it, vi } from 'vitest'
import { defineComponent } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import ConnectionProtectionPanel from './ConnectionProtectionPanel.vue'
import { api } from '../lib/api'

vi.mock('../lib/api', () => ({ api: vi.fn(), jsonBody: (value: unknown) => JSON.stringify(value) }))
vi.mock('../stores/auth', () => ({ useAuthStore: () => ({ can: () => true }) }))
vi.mock('vue-router', () => ({ onBeforeRouteLeave: vi.fn() }))

const Button = defineComponent({
  name: 'VBtn', props: ['disabled', 'loading'], emits: ['click'],
  template: '<button :disabled="disabled || loading" @click="$emit(`click`)"><slot /></button>',
})
const Switch = defineComponent({
  name: 'VSwitch', props: ['modelValue'], emits: ['update:modelValue'],
  template: '<button class="qa-switch" @click="$emit(`update:modelValue`, !modelValue)">{{ modelValue }}</button>',
})
const SlotWrapper = defineComponent({ name: 'VAlert', template: '<div><slot /></div>' })
const settings = {
  enabled: true,
  smtpLogin: { windowMinutes: 15, maximumFailures: 5, blockMinutes: 30 },
  imapLogin: { windowMinutes: 15, maximumFailures: 5, blockMinutes: 30 },
  smtpInbound: { perMinute: 120, burst: 30, maximumConcurrent: 16 },
  smtpSubmission: { perMinute: 60, burst: 20, maximumConcurrent: 10 },
  imap: { perMinute: 60, burst: 20, maximumConcurrent: 20 },
  maximumTrackedSources: 4096, tlsHandshakeSeconds: 10, preAuthenticationSeconds: 120,
}
function button(wrapper: ReturnType<typeof mount>, label: string) {
  return wrapper.findAll('button').find(x => x.text().trim() === label)!
}
afterEach(() => vi.restoreAllMocks())

describe('connection protection settings', () => {
  it('shows process state and saves an edited setting', async () => {
    vi.mocked(api).mockImplementation(async (path, init) => {
      if (path === '/security/connection-protection/status')
        return { processes: [{ instanceName: 'mail-1', roles: 'smtp,imap', lastSeenAt: new Date().toISOString(), state: 'applying', trackedSources: 7 }] } as never
      if (init?.method === 'PUT') return JSON.parse(String(init.body)) as never
      return settings as never
    })
    const wrapper = mount(ConnectionProtectionPanel, { global: { stubs: {
      VBtn: Button, VSwitch: Switch, VTextField: true, VAlert: SlotWrapper,
    } } })
    await flushPromises()
    expect(wrapper.text()).toContain('应用中')
    expect(wrapper.text()).toContain('mail-1')
    await wrapper.find('.qa-switch').trigger('click')
    await button(wrapper, '保存防护设置').trigger('click')
    await flushPromises()
    const save = vi.mocked(api).mock.calls.find(x => x[1]?.method === 'PUT')!
    expect(JSON.parse(String(save[1]!.body)).enabled).toBe(false)
    expect(wrapper.text()).toContain('连接防护设置已保存')
    wrapper.unmount()
  })
})
