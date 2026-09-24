// Native-vs-canonical worktree path identity: the native app may report a
// worker through a symlinked alias of the configured worktree root.
import assert from 'node:assert/strict';
import { access, mkdir, mkdtemp, realpath, rm, symlink } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import {
  canonicalPath, mainCheckoutFromGitDirectory, resolveWorktreePath, samePath, strictlyWithin,
} from '../ralph-worktree-path.mjs';

async function layout(t) {
  const base = await realpath(await mkdtemp(path.join(os.tmpdir(), 'ralph-worktree-path-')));
  t.after(() => rm(base, { recursive: true, force: true }));
  const src = path.join(base, 'Volumes', 'data', 'src');
  const root = path.join(src, 'copilot-worktrees', 'pfarm1');
  const main = path.join(src, 'pfarm1');
  const outside = path.join(base, 'outside');
  for (const directory of [root, path.join(main, '.git'), outside]) await mkdir(directory, { recursive: true });
  // /Users/me/s -> /Volumes/data/src, as on the live Mac.
  const aliasSrc = path.join(base, 'Users', 'me', 's');
  await mkdir(path.dirname(aliasSrc), { recursive: true });
  await symlink(src, aliasSrc);
  const aliasRoot = path.join(aliasSrc, 'copilot-worktrees', 'pfarm1');
  return { base, src, root, main, outside, aliasSrc, aliasRoot };
}

test('canonicalPath resolves aliases, nonexistent targets through the nearest ancestor and rejects dangling links', async (t) => {
  const l = await layout(t);
  assert.equal(await canonicalPath(l.aliasRoot), l.root);
  assert.equal(await canonicalPath(path.join(l.aliasRoot, 'not-yet', 'created')), path.join(l.root, 'not-yet', 'created'));
  assert.equal(await canonicalPath(l.root), l.root);
  await symlink(path.join(l.base, 'nowhere'), path.join(l.root, 'dangling'));
  await assert.rejects(canonicalPath(path.join(l.root, 'dangling')), /dangling symlink/);
  await assert.rejects(canonicalPath(path.join(l.aliasRoot, 'dangling', 'child')), /dangling symlink/);
  for (const bad of ['relative/path', `${l.root}/../pfarm1`, `${l.root}//x`, '', undefined]) {
    await assert.rejects(canonicalPath(bad), /absolute, normalized/, String(bad));
  }
});

test('resolveWorktreePath accepts native or canonical spellings and returns one canonical path', async (t) => {
  const l = await layout(t);
  const worker = path.join(l.root, 'jpapiez-crispy-eureka');
  await mkdir(worker);
  for (const [root, target] of [[l.root, worker], [l.root, path.join(l.aliasRoot, 'jpapiez-crispy-eureka')],
    [l.aliasRoot, worker], [l.aliasRoot, path.join(l.aliasRoot, 'jpapiez-crispy-eureka')]]) {
    assert.equal(await resolveWorktreePath(root, target), worker, `${root} ${target}`);
  }
  assert.equal(await resolveWorktreePath(l.root, path.join(l.aliasRoot, 'future')), path.join(l.root, 'future'));
});

test('resolveWorktreePath rejects symlink escapes, the root itself and main-checkout aliases', async (t) => {
  const l = await layout(t);
  await symlink(l.outside, path.join(l.root, 'escape'));
  await symlink(l.main, path.join(l.root, 'main-alias'));
  await assert.rejects(resolveWorktreePath(l.root, path.join(l.aliasRoot, 'escape')), /escapes the configured Ralph worktree root/);
  await assert.rejects(resolveWorktreePath(l.root, path.join(l.root, 'escape', 'nested')), /escapes the configured Ralph worktree root/);
  await assert.rejects(resolveWorktreePath(l.root, path.join(l.root, 'main-alias')), /escapes/);
  await assert.rejects(resolveWorktreePath(l.root, path.join(l.aliasSrc, 'pfarm1')), /escapes/, 'native alias of the main checkout');
  await assert.rejects(resolveWorktreePath(l.root, l.aliasRoot), /escapes/, 'the root itself is never a worker');
  await assert.rejects(resolveWorktreePath(l.root, `${l.root}/../pfarm1`), /absolute, normalized/);
  // A primary checkout placed under the root is still a main checkout, as is anything inside it.
  const primary = path.join(l.root, 'primary');
  await mkdir(path.join(primary, '.git'), { recursive: true });
  await assert.rejects(resolveWorktreePath(l.root, path.join(l.aliasRoot, 'primary')), /main checkout \(its \.git is a directory\)/);
  await assert.rejects(resolveWorktreePath(l.root, path.join(primary, 'src')), /main checkout/);
  // The role's own checkout and the main checkout behind it are excluded, by any spelling.
  const own = path.join(l.root, 'role-consumer');
  await mkdir(own);
  const exclude = [path.join(l.aliasRoot, 'role-consumer'), l.main];
  await assert.rejects(resolveWorktreePath(l.root, own, { exclude }), /aliases the main checkout or another Ralph checkout/);
  await assert.rejects(resolveWorktreePath(l.root, path.join(l.aliasRoot, 'role-consumer', 'x'), { exclude }), /another Ralph checkout/);
  assert.equal(await resolveWorktreePath(l.root, path.join(l.aliasRoot, 'worker'), { exclude }), path.join(l.root, 'worker'));
  await assert.rejects(resolveWorktreePath('relative', own), /worktree root required/);
});

// A case-sensitive volume in memory: directories and symlinks keyed by exact spelling.
function caseSensitiveFs(directories, links = {}, p = path) {
  const enoent = () => Object.assign(new Error('ENOENT'), { code: 'ENOENT' });
  const resolve = (target, depth = 0) => {
    if (depth > 16) throw Object.assign(new Error('ELOOP'), { code: 'ELOOP' });
    const { root } = p.parse(target);
    let current = root;
    for (const part of target.slice(root.length).split(p.sep).filter(Boolean)) {
      const next = p.join(current, part);
      if (links[next]) current = resolve(links[next], depth + 1);
      else if (directories.has(next)) current = next;
      else throw enoent();
    }
    return current;
  };
  return {
    realpath: async (target) => resolve(target),
    lstat: async (target) => {
      if (links[target]) return { isDirectory: () => false };
      if (directories.has(target)) return { isDirectory: () => true };
      throw enoent();
    },
  };
}

test('canonical identity never folds case: a case-sensitive sibling of the root is an escape', async () => {
  const fs = caseSensitiveFs(new Set(['/sandbox', '/sandbox/worktrees', '/sandbox/worktrees/worker', '/sandbox/worktrees/Worker',
    '/sandbox/WORKTREES', '/sandbox/WORKTREES/outside']), { '/sandbox/worktrees/escape': '/sandbox/WORKTREES/outside' });
  await assert.rejects(resolveWorktreePath('/sandbox/worktrees', '/sandbox/WORKTREES/outside', { fs }), /escapes/);
  await assert.rejects(resolveWorktreePath('/sandbox/worktrees', '/sandbox/worktrees/escape', { fs }), /escapes/);
  await assert.rejects(resolveWorktreePath('/sandbox/WORKTREES', '/sandbox/worktrees/worker', { fs }), /escapes/);
  // Two workers whose names differ only by case are two worktrees.
  assert.equal(await resolveWorktreePath('/sandbox/worktrees', '/sandbox/worktrees/Worker', { fs }), '/sandbox/worktrees/Worker');
  assert.equal(await resolveWorktreePath('/sandbox/worktrees', '/sandbox/worktrees/worker', { fs }), '/sandbox/worktrees/worker');
  assert.equal(samePath('/sandbox/worktrees/Worker', '/sandbox/worktrees/worker'), false);
});

test('Windows containment is exact too: path.win32.relative case folding never admits a sibling root', async () => {
  const win = path.win32;
  // The hazard: win32.relative lowercases both sides, so a relative-based check would accept this.
  assert.equal(win.relative('C:\\sandbox\\worktrees', 'C:\\sandbox\\WORKTREES\\outside'), 'outside');
  const options = { pathApi: win };
  assert.equal(strictlyWithin('C:\\sandbox\\worktrees', 'C:\\sandbox\\WORKTREES\\outside', options), false);
  assert.equal(strictlyWithin('C:\\sandbox\\worktrees', 'C:\\sandbox\\worktrees\\worker', options), true);
  assert.equal(strictlyWithin('C:\\sandbox\\worktrees', 'C:\\sandbox\\worktreesX\\worker', options), false);
  assert.equal(strictlyWithin('C:\\sandbox\\worktrees', 'C:\\sandbox\\worktrees', options), false);
  assert.equal(strictlyWithin('C:\\', 'C:\\worker', options), true);
  const fs = caseSensitiveFs(new Set(['C:\\sandbox', 'C:\\sandbox\\worktrees', 'C:\\sandbox\\worktrees\\worker',
    'C:\\sandbox\\WORKTREES', 'C:\\sandbox\\WORKTREES\\outside', 'C:\\alias']),
  { 'C:\\sandbox\\worktrees\\escape': 'C:\\sandbox\\WORKTREES\\outside', 'C:\\alias\\wt': 'C:\\sandbox\\worktrees' }, win);
  const resolve = (root, target) => resolveWorktreePath(root, target, { fs, pathApi: win });
  await assert.rejects(resolve('C:\\sandbox\\worktrees', 'C:\\sandbox\\WORKTREES\\outside'), /escapes/);
  await assert.rejects(resolve('C:\\sandbox\\worktrees', 'C:\\sandbox\\worktrees\\escape'), /escapes/);
  await assert.rejects(resolve('C:\\sandbox\\worktrees', 'C:\\sandbox\\worktrees\\escape\\nested'), /escapes/);
  // A genuine alias of the root still converges on the canonical spelling.
  assert.equal(await resolve('C:\\sandbox\\worktrees', 'C:\\alias\\wt\\worker'), 'C:\\sandbox\\worktrees\\worker');
  assert.equal(await resolve('C:\\alias\\wt', 'C:\\sandbox\\worktrees\\new-worker'), 'C:\\sandbox\\worktrees\\new-worker');
});

test('a case alias on a case-insensitive volume converges through realpath, not folding', async (t) => {
  const l = await layout(t);
  const worker = path.join(l.root, 'jpapiez-crispy-eureka');
  await mkdir(worker);
  const upper = path.join(path.dirname(l.root), 'PFARM1', 'jpapiez-crispy-eureka');
  const insensitive = await access(upper).then(() => true, () => false);
  if (insensitive) assert.equal(await resolveWorktreePath(l.root, upper), worker, 'realpath returns the on-disk spelling');
  else await assert.rejects(resolveWorktreePath(l.root, upper), /escapes/, 'a case-sensitive volume keeps the spellings distinct');
});

test('path helpers compare canonical forms exactly and derive the main checkout of a linked worktree', () => {
  assert.equal(samePath('/Volumes/Data/x', '/Volumes/Data/x'), true);
  assert.equal(samePath('/Volumes/Data/x', '/volumes/data/x'), false);
  assert.equal(samePath(undefined, undefined), false);
  assert.equal(strictlyWithin('/w', '/w/a'), true);
  assert.equal(strictlyWithin('/w', '/W/a'), false);
  assert.equal(strictlyWithin('/w', '/w'), false);
  assert.equal(strictlyWithin('/w', '/wx/a'), false);
  assert.equal(strictlyWithin('/w', '/w/..hidden'), true, 'a name starting with .. is not a parent reference');
  assert.equal(mainCheckoutFromGitDirectory('/src/pfarm1/.git/worktrees/role'), '/src/pfarm1');
  assert.equal(mainCheckoutFromGitDirectory('/role/git'), undefined);
  assert.equal(mainCheckoutFromGitDirectory(undefined), undefined);
});
