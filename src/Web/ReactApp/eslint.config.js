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
      reactHooks.configs['recommended-latest'],
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
    },
  },
])
