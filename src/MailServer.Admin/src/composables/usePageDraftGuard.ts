import { onBeforeUnmount, ref, type Ref } from 'vue'
import { onBeforeRouteLeave, onBeforeRouteUpdate } from 'vue-router'

export function usePageDraftGuard(dirty: Readonly<Ref<boolean>>, busy: Readonly<Ref<boolean>>) {
  const leaveOpen = ref(false)
  let resolveLeave: ((value: boolean) => void) | undefined
  function decideLeave(value: boolean) { leaveOpen.value = false; resolveLeave?.(value); resolveLeave = undefined }
  function guard() {
    if (busy.value) return false
    if (!dirty.value) return true
    resolveLeave?.(false)
    leaveOpen.value = true
    return new Promise<boolean>(resolve => { resolveLeave = resolve })
  }
  onBeforeRouteLeave(guard)
  onBeforeRouteUpdate(guard)
  function beforeUnload(event: BeforeUnloadEvent) { if (dirty.value || busy.value) { event.preventDefault(); event.returnValue = '' } }
  window.addEventListener('beforeunload', beforeUnload)
  onBeforeUnmount(() => { window.removeEventListener('beforeunload', beforeUnload); resolveLeave?.(false) })
  return { leaveOpen, decideLeave }
}
