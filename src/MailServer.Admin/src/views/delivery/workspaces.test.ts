import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent } from 'vue'
import { flushPromises, shallowMount } from '@vue/test-utils'
import QueueWorkspace from './QueueWorkspace.vue'
import TlsReportsWorkspace from './TlsReportsWorkspace.vue'
import DeliveryJobDetailDialog from './DeliveryJobDetailDialog.vue'
import { api } from '../../lib/api'
import type { DeliveryJob } from './types'

vi.mock('../../lib/api', () => ({ api: vi.fn() }))
vi.mock('../../composables/usePermission', () => ({ usePermission: () => ({ can: () => true }) }))

const Input = defineComponent({
  name: 'SearchInput',
  props: ['modelValue'], emits: ['update:modelValue'], template: '<div />',
})
const Table = defineComponent({ template: '<table><slot /></table>' })
const Checkbox = defineComponent({
  name: 'TestCheckbox',
  props: ['modelValue', 'disabled', 'ariaLabel'], emits: ['update:modelValue'],
  template: '<button :disabled="disabled" :aria-label="ariaLabel" @click="$emit(\'update:modelValue\', !modelValue)" />',
})
const mounted: Array<{ unmount: () => void }> = []
const result = (totalCount: number) => ({ items: [], totalCount, page: 1, pageSize: 20 })

function create(component: typeof QueueWorkspace | typeof TlsReportsWorkspace) {
  const wrapper = shallowMount(component, {
    props: component === QueueWorkspace ? { mode: 'queue', refreshKey: 0 } : { refreshKey: 0 },
    global: { stubs: {
      VTextField: Input, VSelect: true, VTable: Table,
      VBtn: true, VAlert: true, VSpacer: true, VCheckboxBtn: Checkbox,
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

it('does not select or offer deletion for a processing delivery', async () => {
  const timestamp = '2026-09-18T12:00:00Z'
  const jobs: DeliveryJob[] = ['Processing', 'Pending'].map((status, index) => ({
    id: `job-${index}`, sender: 'sender@example.com', recipient: 'recipient@example.net',
    status: status as DeliveryJob['status'], attempts: 0, nextAttemptAt: timestamp,
    createdAt: timestamp, updatedAt: timestamp,
  }))
  vi.mocked(api).mockResolvedValueOnce({ items: jobs, totalCount: 2, page: 1, pageSize: 20 })
  const wrapper = create(QueueWorkspace)
  await flushPromises()
  const checkboxes = wrapper.findAllComponents(Checkbox)
  expect(checkboxes).toHaveLength(3)
  expect(checkboxes[1]!.props('disabled')).toBe(true)
  checkboxes[0]!.vm.$emit('update:modelValue', true)
  await wrapper.vm.$nextTick()
  expect(wrapper.text()).toContain('已选择 1 个')
  expect(wrapper.findAll('tr')[1]!.text()).not.toContain('删除投递任务')
})

it('distinguishes an attempts request failure from an empty attempt history', async () => {
  const timestamp = '2026-09-18T12:00:00Z'
  const job: DeliveryJob = {
    id: 'job-1', sender: 'sender@example.com', recipient: 'recipient@example.net',
    status: 'Deferred', attempts: 1, nextAttemptAt: timestamp, createdAt: timestamp, updatedAt: timestamp,
  }
  vi.mocked(api).mockRejectedValueOnce(new Error('请求失败'))
  const wrapper = shallowMount(DeliveryJobDetailDialog, {
    props: { modelValue: true, job },
    global: { stubs: {
      VDialog: { template: '<div><slot /></div>' }, VCard: { template: '<div><slot /></div>' },
      VWindow: { template: '<div><slot /></div>' }, VWindowItem: { template: '<div><slot /></div>' },
      VCardText: { template: '<div><slot /></div>' }, VTabs: true, VTab: true,
      VBtn: true, VIcon: true, VAlert: true, VProgressLinear: true,
    } },
  })
  mounted.push(wrapper)
  await flushPromises()
  expect(wrapper.text()).toContain('投递尝试加载失败')
  expect(wrapper.text()).not.toContain('尚无投递尝试')
})

it('labels a sanitized and truncated delivery session summary', async () => {
  const timestamp = '2026-09-18T12:00:00Z'
  const job: DeliveryJob = {
    id: 'job-1', sender: 'sender@example.com', recipient: 'recipient@example.net',
    status: 'Deferred', attempts: 1, nextAttemptAt: timestamp, createdAt: timestamp, updatedAt: timestamp,
  }
  vi.mocked(api).mockResolvedValueOnce([{
    id: 'attempt-1', attemptNumber: 1, succeeded: false, permanentFailure: false,
    stage: 'SMTP', durationMilliseconds: 10, response: '451 temporary', createdAt: timestamp,
    sessionSummary: '[敏感信息已脱敏]\n[地址已脱敏]\n[摘要已截断至 64 KiB]',
  }])
  const wrapper = shallowMount(DeliveryJobDetailDialog, {
    props: { modelValue: true, job },
    global: { stubs: {
      VDialog: { template: '<div><slot /></div>' }, VCard: { template: '<div><slot /></div>' },
      VWindow: { template: '<div><slot /></div>' }, VWindowItem: { template: '<div><slot /></div>' },
      VCardText: { template: '<div><slot /></div>' }, VTabs: true, VTab: true,
      VBtn: true, VIcon: true, VAlert: true, VProgressLinear: true,
      StatusPill: true,
    } },
  })
  mounted.push(wrapper)
  await flushPromises()
  expect(wrapper.text()).toContain('查看会话摘要')
  expect(wrapper.text()).toContain('· 已脱敏')
  expect(wrapper.text()).toContain('· 已截断')
})
