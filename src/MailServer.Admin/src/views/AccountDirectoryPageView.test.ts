import { describe, expect, it, vi } from 'vitest'
import { shallowMount } from '@vue/test-utils'
import AccountDirectoryPageView from './AccountDirectoryPageView.vue'
import AccountDirectoryView from './AccountDirectoryView.vue'
import DomainDetailView from './DomainDetailView.vue'

const access = vi.hoisted(() => ({ canReadDomain: true }))
vi.mock('vue-router', () => ({ useRoute: () => ({ params: { id: 'domain-a' } }) }))
vi.mock('../composables/usePermission', () => ({ usePermission: () => ({ can: (permission: string, domainId: string) => permission === 'domain.read' && domainId === 'domain-a' && access.canReadDomain }) }))

describe('account directory parent layout', () => {
  it('shows the directory inside domain details when domain access is granted', () => {
    access.canReadDomain = true
    const wrapper = shallowMount(AccountDirectoryPageView)
    expect(wrapper.findComponent(DomainDetailView).exists()).toBe(true)
    expect(wrapper.findComponent(AccountDirectoryView).exists()).toBe(false)
  })

  it('keeps account and alias access available without exposing domain details', () => {
    access.canReadDomain = false
    const wrapper = shallowMount(AccountDirectoryPageView)
    expect(wrapper.findComponent(AccountDirectoryView).exists()).toBe(true)
    expect(wrapper.findComponent(DomainDetailView).exists()).toBe(false)
  })
})
