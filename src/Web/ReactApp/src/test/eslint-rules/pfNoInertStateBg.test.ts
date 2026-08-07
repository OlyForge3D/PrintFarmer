import { RuleTester } from 'eslint'
import { describe, it } from 'vitest'
import rule from '../../../eslint-rules/pf-no-inert-state-bg.js'

RuleTester.describe = describe
RuleTester.it = it

const ruleTester = new RuleTester({
  languageOptions: {
    ecmaVersion: 2022,
    sourceType: 'module',
    parserOptions: { ecmaFeatures: { jsx: true } },
  },
})

const overflowingClassExpression = `<div className={clsx(${Array.from(
  { length: 8 },
  (_, index) => `condition${index} && "bg-pf-bg-${index}"`,
).join(', ')})} />`

ruleTester.run('pf-no-inert-state-bg', rule, {
  valid: [
    {
      code: '<button className="bg-pf-bg-2 hover:bg-pf-bg-1" />',
    },
    {
      code: '<button className="bg-pf-bg-1/55 hover:bg-pf-bg-1/75" />',
    },
    {
      code: '<button className={isDragging ? "bg-pf-accent" : "hover:bg-pf-accent"} />',
    },
    {
      code: '<input className="border-pf-error focus:border-pf-error" />',
    },
    {
      code: '<div className="group hover:bg-pf-bg-2"><span className="bg-pf-bg-1 group-hover:bg-pf-bg-1" /></div>',
    },
    {
      code: '<span className="bg-pf-bg-1 peer-hover:bg-pf-bg-1" />',
    },
    {
      code: '<button className="bg-pf-bg-1 hover:bg-pf-bg-2 disabled:hover:bg-pf-bg-1" />',
    },
    {
      code: '<button className={clsx("bg-pf-bg-1", enabled && "hover:bg-pf-bg-2")} />',
    },
    {
      code: '<button className={cn(["bg-pf-bg-1", active && "active:bg-pf-bg-0"])} />',
    },
    {
      code: '<button className={clsx({ "bg-pf-bg-1": selected, "hover:bg-pf-bg-2": interactive })} />',
    },
    {
      code: '<button className={`bg-pf-bg-1 ${selected ? "hover:bg-pf-bg-2" : "hover:bg-pf-bg-0"}`} />',
    },
    {
      code: '<button className="bg-[color:var(--pf-surface)] hover:bg-[color:var(--pf-surface-hover)]" />',
    },
    {
      code: '<button className="bg-pf-bg-1/50 hover:bg-pf-bg-1/[.75]" />',
    },
    {
      code: '<button className="-bg-[position:1px] hover:-bg-[position:1px]" />',
    },
    {
      code: '<button className="hover:bg-pf-bg-1 focus:bg-pf-bg-1" />',
    },
    {
      code: '<button data-pf-allow-inert-bg="selected state deliberately stays pinned" className="bg-pf-accent-bg hover:bg-pf-accent-bg" />',
    },
    {
      code: '<button data-pf-allow-inert-bg={false} className="bg-pf-bg-1 hover:bg-pf-bg-2" />',
    },
    {
      code: '<button className="bg-(--pf-surface) hover:bg-(--pf-surface-hover)" />',
    },
  ],
  invalid: [
    {
      name: '#1082 SetupWizard discovered instance mutation',
      code: '<button className="w-full text-left p-2 bg-pf-bg-2 hover:bg-pf-bg-2 border border-pf-border" />',
      errors: [
        {
          messageId: 'inert',
          data: {
            base: 'bg-pf-bg-2',
            variant: 'hover:bg-pf-bg-2',
            state: 'hover',
          },
        },
      ],
    },
    {
      name: '#1082 SetupWizard test URL mutation',
      code: '<button className="px-3 py-2 bg-pf-accent-bg hover:bg-pf-accent-bg disabled:opacity-50" />',
      errors: [
        {
          messageId: 'inert',
          data: {
            base: 'bg-pf-accent-bg',
            variant: 'hover:bg-pf-accent-bg',
            state: 'hover',
          },
        },
      ],
    },
    {
      name: '#1082 ExplorerView active mutation',
      code: '<div className="w-1 bg-pf-border hover:bg-pf-accent active:bg-pf-accent" />',
      errors: [
        {
          messageId: 'inert',
          data: {
            base: 'hover:bg-pf-accent',
            variant: 'active:bg-pf-accent',
            state: 'active',
          },
        },
      ],
    },
    {
      code: '<button className="dark:md:bg-pf-bg-1 dark:md:hover:bg-pf-bg-1" />',
      errors: [
        {
          messageId: 'inert',
          data: {
            base: 'dark:md:bg-pf-bg-1',
            variant: 'dark:md:hover:bg-pf-bg-1',
            state: 'hover',
          },
        },
      ],
    },
    {
      code: '<button className="!bg-pf-bg-1 hover:!bg-pf-bg-1" />',
      errors: [
        {
          messageId: 'inert',
          data: {
            base: '!bg-pf-bg-1',
            variant: 'hover:!bg-pf-bg-1',
            state: 'hover',
          },
        },
      ],
    },
    {
      code: '<button className="!bg-pf-bg-1 hover:bg-pf-bg-2" />',
      errors: [
        {
          messageId: 'inert',
          data: {
            base: '!bg-pf-bg-1',
            variant: 'hover:bg-pf-bg-2',
            state: 'hover',
          },
        },
      ],
    },
    {
      code: '<button className="bg-pf-bg-1 hover:!bg-pf-bg-1" />',
      errors: [
        {
          messageId: 'inert',
          data: {
            base: 'bg-pf-bg-1',
            variant: 'hover:!bg-pf-bg-1',
            state: 'hover',
          },
        },
      ],
    },
    {
      code: '<button className="bg-pf-bg-1! hover:bg-pf-bg-1!" />',
      errors: [
        {
          messageId: 'inert',
          data: {
            base: 'bg-pf-bg-1!',
            variant: 'hover:bg-pf-bg-1!',
            state: 'hover',
          },
        },
      ],
    },
    {
      code: '<button className="bg-[color:var(--pf-surface)] active:bg-[color:var(--pf-surface)]" />',
      errors: [
        {
          messageId: 'inert',
          data: {
            base: 'bg-[color:var(--pf-surface)]',
            variant: 'active:bg-[color:var(--pf-surface)]',
            state: 'active',
          },
        },
      ],
    },
    {
      code: '<button className={clsx("bg-pf-bg-1", enabled && "enabled:hover:bg-pf-bg-1")} />',
      errors: [
        {
          messageId: 'inert',
          data: {
            base: 'bg-pf-bg-1',
            variant: 'enabled:hover:bg-pf-bg-1',
            state: 'hover',
          },
        },
      ],
    },
    {
      code: '<button className="bg-(--pf-surface) hover:bg-(--pf-surface)" />',
      errors: [
        {
          messageId: 'inert',
          data: {
            base: 'bg-(--pf-surface)',
            variant: 'hover:bg-(--pf-surface)',
            state: 'hover',
          },
        },
      ],
    },
    {
      code: overflowingClassExpression,
      errors: [{ messageId: 'analysisLimit' }],
    },
  ],
})
