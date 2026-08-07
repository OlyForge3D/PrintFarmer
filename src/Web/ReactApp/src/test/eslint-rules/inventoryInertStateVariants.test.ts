import { execFileSync } from 'node:child_process'
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import path from 'node:path'
import { afterAll, describe, expect, it } from 'vitest'

interface InventoryMatch {
  file: string
  line: number
  column: number
  family: string
  base: string
  variant: string
}

interface InventoryResult {
  dirty: boolean
  worktreeStatus: Array<{
    status: string
    path: string
    originalPath?: string
  }>
  totals: {
    production: number
    tests: number
    byFamily: Record<string, number>
  }
  production: InventoryMatch[]
}

const inventoryScript = path.resolve('eslint-rules/inventory-inert-state-variants.js')
const fixtureRoot = mkdtempSync(path.join(tmpdir(), 'pf-inert-inventory-'))
const sourceFile = path.join(fixtureRoot, 'src', 'fixture.tsx')

function git(...args: string[]) {
  return execFileSync('git', args, { cwd: fixtureRoot, encoding: 'utf8' })
}

function runInventory() {
  return JSON.parse(
    execFileSync(process.execPath, [inventoryScript], {
      cwd: fixtureRoot,
      encoding: 'utf8',
    }),
  ) as InventoryResult
}

mkdirSync(path.dirname(sourceFile), { recursive: true })
writeFileSync(sourceFile, `
export function Fixture({ first, maybe }) {
  return <>
    <div className={(first && "bg-pf-bg-1 hover:bg-pf-bg-1") || "bg-pf-bg-2 hover:bg-pf-bg-2"} />
    <div className={maybe("border-pf-error focus:border-pf-error") ?? "text-pf-error hover:text-pf-error"} />
    <i className="text-pf-error hover:text-pf-error" /><b className="text-pf-error hover:text-pf-error" />
  </>
}
`)
git('init', '--quiet')
git('config', 'user.name', 'Inventory Test')
git('config', 'user.email', 'inventory@example.invalid')
git('config', 'core.autocrlf', 'false')
git('add', 'src/fixture.tsx')
git('commit', '--quiet', '-m', 'fixture')

afterAll(() => {
  rmSync(fixtureRoot, { recursive: true, force: true })
})

describe('inert state inventory', () => {
  it('enumerates both logical branches and preserves same-line sites by column', () => {
    const result = runInventory()

    expect(result.dirty).toBe(false)
    expect(result.worktreeStatus).toEqual([])
    expect(result.totals).toEqual({
      production: 6,
      tests: 0,
      byFamily: {
        bg: 2,
        border: 1,
        text: 3,
      },
    })

    const sameLineTextSites = result.production.filter(match =>
      match.family === 'text' && match.line === 6,
    )
    expect(sameLineTextSites).toHaveLength(2)
    expect(new Set(sameLineTextSites.map(match => match.column)).size).toBe(2)
  })

  it('reports complete modified and untracked porcelain entries', () => {
    writeFileSync(sourceFile, `${git('show', 'HEAD:src/fixture.tsx')}\n// modified\n`)
    const untrackedFile = path.join(fixtureRoot, 'notes', 'inventory.txt')
    mkdirSync(path.dirname(untrackedFile), { recursive: true })
    writeFileSync(untrackedFile, 'untracked\n')

    const result = runInventory()

    expect(result.dirty).toBe(true)
    expect(result.worktreeStatus).toEqual([
      { status: '??', path: 'notes/inventory.txt' },
      { status: ' M', path: 'src/fixture.tsx' },
    ])
  })
})
