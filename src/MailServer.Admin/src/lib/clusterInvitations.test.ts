import { describe, expect, it } from 'vitest'
import { availableInvitationRoles, invitationStatus, type ClusterInvitation } from './clusterInvitations'

const now = Date.parse('2026-09-17T06:00:00Z')
const invitation: ClusterInvitation = { id: 'invite', role: 'Witness', expiresAt: '2026-09-17T06:15:00Z', used: false, revoked: false }

describe('cluster invitation presentation', () => {
  it('offers only unoccupied joinable roles, never primary', () => {
    expect(availableInvitationRoles([{ role: 'Primary' }]).map(x => x.value)).toEqual(['Standby', 'Witness'])
    expect(availableInvitationRoles([{ role: 'Standby' }]).map(x => x.value)).toEqual(['Witness'])
    expect(availableInvitationRoles([{ role: 'Witness' }, { role: 'Standby' }])).toEqual([])
  })
  it('expires exactly at the deadline', () => {
    expect(invitationStatus(invitation, now)).toBe('待使用')
    expect(invitationStatus(invitation, Date.parse(invitation.expiresAt))).toBe('已过期')
  })
  it('preserves used and revoked states after expiration', () => {
    expect(invitationStatus({ ...invitation, used: true }, now + 3600000)).toBe('已使用')
    expect(invitationStatus({ ...invitation, used: true, revoked: true }, now)).toBe('已撤销')
  })
  it('never presents an invalid expiration as usable', () => {
    expect(invitationStatus({ ...invitation, expiresAt: 'invalid' }, now)).toBe('有效期未知')
  })
})
