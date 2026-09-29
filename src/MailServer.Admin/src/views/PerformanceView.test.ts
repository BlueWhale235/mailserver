import { afterEach, describe, expect, it, vi } from 'vitest'
import { defineComponent } from 'vue'
import { flushPromises, shallowMount } from '@vue/test-utils'
import PerformanceView from './PerformanceView.vue'
import { api } from '../lib/api'
import { canAccessPage, getPageDefinition } from '../lib/pageAccess'

vi.mock('../lib/api', () => ({ api: vi.fn() }))
let manage = false
vi.mock('../stores/auth', () => ({ useAuthStore: () => ({ can: (code: string) => code === 'performance.read' || manage }) }))
const Slot = defineComponent({ template: '<div><slot /></div>' })
const Button = defineComponent({ emits: ['click'], template: '<button @click="$emit(`click`)"><slot /></button>' })
const Dialog = defineComponent({ props: ['modelValue'], template: '<div v-if="modelValue"><slot /></div>' })
function mount() { return shallowMount(PerformanceView, { global: { stubs: { PageHeader: Slot, VAlert: Slot, VTabs: Slot, VTab: Slot, VCard: Slot, VTable: Slot, VBtn: Button, VDialog: Dialog } } }) }
afterEach(() => { vi.resetAllMocks(); manage = false })

describe('performance analysis', () => {
  it('loads slow operations and exposes incomplete-data indicators and stage details', async () => {
    const operation = { id: 'one', traceId: 'trace', module: 'imap', name: 'FETCH', status: 'ok', startedAt: '2026-09-29T00:00:00Z', durationMilliseconds: 100, processingMilliseconds: 60, sqlMilliseconds: 30, sqlCount: 2, truncated: true }
    vi.mocked(api).mockImplementation(async path => {
      if (path.includes('/operations?')) return { items: [operation], nextCursor: null } as never
      if (path.endsWith('/operations/one')) return { operation, stages: [{ id: 's1', name: 'mailbox.snapshot', category: 'processing', offsetMilliseconds: 5, durationMilliseconds: 50, selfMilliseconds: 20, status: 'ok' }], related: [], sql: [] } as never
      if (path.endsWith('/settings')) return { enabled: true, sampleRate: .01 } as never
      return { rankings: [], sql: [], processes: [{ processId: 'p', droppedOperations: 2 }] } as never
    })
    const wrapper = mount(); await flushPromises()
    expect(api).toHaveBeenCalledWith('/operations/performance/operations?slowOnly=true')
    expect(wrapper.text()).toContain('采集存在丢弃')
    expect(wrapper.text()).toContain('FETCH')
    await wrapper.findAll('button').find(x => x.text() === '查看')!.trigger('click'); await flushPromises()
    expect(wrapper.text()).toContain('mailbox.snapshot')
    expect(wrapper.text()).toContain('阶段详情已截断')
    wrapper.unmount()
  })
  it('shows API failures without disguising them as empty data', async () => {
    vi.mocked(api).mockRejectedValue(new Error('数据库暂时不可用'))
    const wrapper = mount(); await flushPromises()
    expect(wrapper.text()).toContain('数据库暂时不可用'); wrapper.unmount()
  })
  it('requires a global read grant for the page', () => {
    expect(canAccessPage(getPageDefinition('performance')!, { can: () => false, canAnyDomain: () => true })).toBe(false)
    expect(canAccessPage(getPageDefinition('performance')!, { can: code => code === 'performance.read', canAnyDomain: () => false })).toBe(true)
  })
})
