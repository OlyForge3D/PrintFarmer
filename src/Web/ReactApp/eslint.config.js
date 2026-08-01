import js from '@eslint/js'
import globals from 'globals'
import reactHooks from 'eslint-plugin-react-hooks'
import reactRefresh from 'eslint-plugin-react-refresh'
import tseslint from 'typescript-eslint'
import { globalIgnores } from 'eslint/config'
import localPlugin from './eslint-rules/eslint-plugin-local.js'

export default tseslint.config([
  globalIgnores(['dist', 'coverage', 'coverage/**', 'coverage/lcov-report/**']),
  {
    files: ['**/*.{ts,tsx}'],
    extends: [
      js.configs.recommended,
      tseslint.configs.recommended,
      reactHooks.configs.flat['recommended-latest'],
      reactRefresh.configs.vite,
    ],
    languageOptions: {
      ecmaVersion: 2020,
      globals: globals.browser,
    },
    plugins: {
      local: localPlugin,
    },
    rules: {
      'local/pf-no-unguarded-console': 'warn',
      'local/pf-no-raw-html-controls': 'warn',
      'local/pf-require-apiclient': 'error',
      // DESIGN-LANGUAGE caps rectangular surfaces at --pf-radius-lg (8px).
      // Repo-wide the ceiling is grandfathered at 12px so the always-wrong
      // radii (2xl/3xl and oversized arbitrary values) are blocked everywhere
      // without a mass rewrite of the ~80 existing `rounded-xl` call sites,
      // and `rounded-full` is left alone because deciding whether each one is
      // a legal tag chip or an illegal pill needs a human. Areas that have
      // completed the migration hold the real line below; lowering `maxPx` to
      // 8 repo-wide is the ratchet that finishes the job.
      'local/pf-no-oversized-radius': ['error', { maxPx: 12, checkFullRound: false }],
    },
  },
  // Admin, settings and the design system were migrated by epic #1005, so they
  // hold the documented ceiling and are checked for pill-shaped surfaces.
  {
    files: [
      'src/features/admin/**/*.{ts,tsx}',
      'src/features/settings/**/*.{ts,tsx}',
      'src/design-system/**/*.{ts,tsx}',
    ],
    rules: {
      'local/pf-no-oversized-radius': ['error', { maxPx: 8, checkFullRound: true }],
    },
  },
  // Exclude api.ts from apiClient rule (it defines apiClient)
  {
    files: ['src/services/api.ts'],
    rules: {
      'local/pf-require-apiclient': 'off',
    },
  },
  // Disable raw HTML controls rule in test files (tests often need raw elements for testing)
  {
    files: ['src/test/**/*.{ts,tsx}', '**/*.test.{ts,tsx}', '**/*.spec.{ts,tsx}'],
    rules: {
      'local/pf-no-raw-html-controls': 'off',
    },
  },
])
