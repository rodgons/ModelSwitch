import js from '@eslint/js'
import skipFormatting from '@vue/eslint-config-prettier/skip-formatting'
import { defineConfigWithVueTs, vueTsConfigs } from '@vue/eslint-config-typescript'
import pluginVue from 'eslint-plugin-vue'
import { globalIgnores } from 'eslint/config'

export default defineConfigWithVueTs(
  globalIgnores(['dist/**', 'coverage/**', 'test-results/**', 'playwright-report/**']),
  { files: ['**/*.{ts,mts,vue}'], extends: [js.configs.recommended] },
  pluginVue.configs['flat/recommended'],
  vueTsConfigs.recommended,
  skipFormatting,
  {
    // design-system.md section 9.2 names the primitives and components (Stack, Inline, Grid, Badge, Money...): they are the vocabulary.
    files: ['src/shared/**/*.vue'],
    rules: { 'vue/multi-word-component-names': 'off' },
  },
)
