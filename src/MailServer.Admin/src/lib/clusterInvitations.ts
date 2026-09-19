export interface ClusterInvitation {
  id: string
  role: 'Standby' | 'Witness'
  expiresAt: string
  used: boolean
  revoked: boolean
  token?: string | null
}

export function invitationStatus(invitation: ClusterInvitation, now: number): string {
  if (invitation.revoked) return '已撤销'
  if (invitation.used) return '已使用'
  const expiration = Date.parse(invitation.expiresAt)
  if (!Number.isFinite(expiration)) return '有效期未知'
  return expiration <= now ? '已过期' : '待使用'
}

export function availableInvitationRoles(nodes: { role: string }[]) {
  return [
    { title: '副本节点', value: 'Standby' as const },
    { title: '见证节点', value: 'Witness' as const },
  ].filter(role => !nodes.some(node => node.role === role.value))
}
