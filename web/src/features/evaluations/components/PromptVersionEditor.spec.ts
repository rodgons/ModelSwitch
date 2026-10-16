import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import PromptVersionEditor from './PromptVersionEditor.vue'

const from = {
  promptId: 'p1',
  version: 3,
  template: {
    messages: [
      { role: 'system', content: 'Reply in {{lang}}.' },
      { role: 'user', content: '{{text}}' },
    ],
    responseFormat: { type: 'json_object' },
  },
  createdAt: '2026-10-16T00:00:00Z',
  createdByAdminId: null,
}

const editor = () =>
  mount(PromptVersionEditor, {
    props: { promptId: 'p1', from },
    global: { plugins: [[VueQueryPlugin, { queryClient: new QueryClient() }]] },
  })

describe('Prompt Version editor', () => {
  it('starts from the version shown and lists its variables', () => {
    const wrapper = editor()
    expect(wrapper.text()).toContain('New version from v3')
    expect(wrapper.findAll('textarea')[1]!.element.value).toBe('{{text}}')
    expect(wrapper.text()).toContain('lang')
    expect(wrapper.text()).toContain('text')
  })

  it('reorders, adds and removes messages', async () => {
    const wrapper = editor()
    await wrapper.find('[aria-label="Move message 2 up"]').trigger('click')
    expect(wrapper.findAll('textarea')[0]!.element.value).toBe('{{text}}')
    await wrapper
      .findAll('button')
      .find((b) => b.text() === 'Add message')!
      .trigger('click')
    expect(wrapper.findAll('[role="group"]')).toHaveLength(3)
    await wrapper.find('[aria-label="Remove message 3"]').trigger('click')
    expect(wrapper.findAll('[role="group"]')).toHaveLength(2)
  })

  it('blocks saving while the response format is not a JSON object', async () => {
    const wrapper = editor()
    const format = wrapper.findAll('textarea').at(-1)!
    await format.setValue('{oops')
    expect(wrapper.text()).toContain('Not valid JSON.')
    const submit = wrapper.findAll('button').find((b) => b.text() === 'Create version')!
    expect(submit.attributes('disabled')).toBeDefined()
  })
})
