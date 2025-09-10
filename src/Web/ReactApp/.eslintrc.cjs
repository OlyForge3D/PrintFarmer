/* ESLint configuration for PrintFarmer React app */
const restrictedImports = [
  {
    name: '@/contexts/useAuth',
    message: 'Import useAuth from \'@/contexts/AuthContext\' instead (shim removed).'
  },
  {
    name: '@/contexts/useTheme',
    message: 'Import useTheme from \'@/contexts/ThemeContext\' instead (shim removed).'
  },
  {
    name: '@/contexts/ThemeHooks',
    message: 'Import theme hooks from \'@/contexts/ThemeContext\'.'
  }
];

module.exports = {
  root: true,
  ignorePatterns: ["dist", "node_modules"],
  extends: [
    'eslint:recommended',
    'plugin:react-hooks/recommended'
  ],
  parser: '@typescript-eslint/parser',
  plugins: ['@typescript-eslint', 'react-refresh'],
  parserOptions: {
    ecmaVersion: 2022,
    sourceType: 'module',
    ecmaFeatures: { jsx: true }
  },
  settings: {
    react: { version: 'detect' }
  },
  rules: {
    'react-refresh/only-export-components': ['error', { allowConstantExport: true }],
    'no-restricted-imports': ['error', { paths: restrictedImports }],
    '@typescript-eslint/no-explicit-any': ['error', { ignoreRestArgs: false }]
  },
  overrides: [
    {
      files: ['src/contexts/*Context.tsx'],
      rules: {
        'react-refresh/only-export-components': 'off'
      }
    }
  ]
};
