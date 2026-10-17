import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { flushPromises, mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { createMemoryHistory, createRouter } from 'vue-router'
import { ApiError } from '@/api/client'
import DeleteAction from './DeleteAction.vue'

/** Stands in for the PrimeVue dialog: shows its props and slot while visible, and confirms on demand. */
const ConfirmDialogStub = defineComponent({
  props: {
    visible: Boolean,
    title: { type: String, default: '' },
    action: { type: String, default: '' },
    confirmText: { type: String, default: undefined },
    pending: Boolean,
  },
  emits: ['confirm', 'update:visible'],
  setup(props, { emit, slots }) {
    return () =>
      props.visible
        ? h('div', { role: 'dialog', 'data-confirm-text': props.confirmText }, [
            h('h2', props.title),
            slots.default?.(),
            h('button', { class: 'confirm', onClick: () => emit('confirm') }, props.action),
          ])
        : null
  },
})

async function mountAction(remove: () => Promise<void>) {
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', component: { render: () => null } },
      { path: '/evaluations', component: { render: () => null } },
    ],
  })
  await router.push('/')
  const queryClient = new QueryClient()
  const invalidate = vi.spyOn(queryClient, 'invalidateQueries')
  const wrapper = mount(DeleteAction, {
    props: {
      kind: 'Dataset',
      name: 'Support triage',
      remove,
      invalidate: ['datasets'],
      to: '/evaluations?tab=datasets',
    },
    slots: { default: '<p>Every Dataset Version and its Cases are deleted.</p>' },
    global: {
      plugins: [router, [VueQueryPlugin, { queryClient }]],
      stubs: { ConfirmDialog: ConfirmDialogStub },
    },
  })
  return { wrapper, router, invalidate }
}

describe('Delete action', () => {
  it('asks first, naming the object and requiring its name to be typed', async () => {
    const remove = vi.fn(async () => {})
    const { wrapper } = await mountAction(remove)
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)

    await wrapper.find('button').trigger('click')

    const dialog = wrapper.find('[role="dialog"]')
    expect(dialog.text()).toContain('Delete dataset Support triage?')
    expect(dialog.text()).toContain('Every Dataset Version and its Cases are deleted.')
    expect(dialog.text()).toContain('This cannot be undone.')
    expect(dialog.attributes('data-confirm-text')).toBe('Support triage')
    expect(remove).not.toHaveBeenCalled()
  })

  it('deletes on confirm, refreshes the lists and leaves for the list page', async () => {
    const remove = vi.fn(async () => {})
    const { wrapper, router, invalidate } = await mountAction(remove)
    await wrapper.find('button').trigger('click')

    await wrapper.find('button.confirm').trigger('click')
    await flushPromises()

    expect(remove).toHaveBeenCalledTimes(1)
    expect(invalidate).toHaveBeenCalledWith({ queryKey: ['datasets'] })
    expect(router.currentRoute.value.fullPath).toBe('/evaluations?tab=datasets')
    expect(wrapper.find('[role="dialog"]').exists()).toBe(false)
  })

  it('keeps the dialog open with the reason when the server refuses', async () => {
    const refused = new ApiError(
      409,
      'conflict',
      'The dataset is used by 2 comparison(s); delete them first.',
      'req-1',
      {},
    )
    const { wrapper, router } = await mountAction(async () => {
      throw refused
    })
    await wrapper.find('button').trigger('click')

    await wrapper.find('button.confirm').trigger('click')
    await flushPromises()

    expect(wrapper.find('[role="dialog"]').text()).toContain('delete them first')
    expect(router.currentRoute.value.fullPath).toBe('/')
  })
})
