import { lstat, realpath } from 'node:fs/promises';
import path from 'node:path';

const defaultPathApi = path;

// Native-vs-canonical worktree path identity. The native app may report a
// worker worktree through a symlinked alias of the configured root (for example
// /Users/me/s -> /Volumes/data/src). Every containment and identity check uses
// the fs.realpath form of both the root and the target; a target that does not
// exist yet resolves through its nearest existing ancestor. The canonical form
// is what Ralph stores, compares and inspects.
//
// Canonical forms are compared exactly. fs.realpath already returns the
// on-disk spelling of every existing component, so a genuine case or symlink
// alias converges on one string. Case-folding by OS name would merge distinct
// directories on a case-sensitive volume, so no comparison folds case. That
// includes containment: path.win32.relative lowercases both sides internally,
// so containment is an exact separator-bounded prefix test instead.

const fail = (message) => { throw new Error(`Native Ralph blocked: ${message}`); };
export const defaultPathFs = Object.freeze({ lstat, realpath });

export const samePath = (left, right) => typeof left === 'string' && left === right;

export function strictlyWithin(root, target, { pathApi = path } = {}) {
  if (typeof root !== 'string' || typeof target !== 'string') return false;
  const base = pathApi.resolve(root);
  const candidate = pathApi.resolve(target);
  const prefix = base.endsWith(pathApi.sep) ? base : `${base}${pathApi.sep}`;
  return candidate !== base && candidate.startsWith(prefix);
}

const overlaps = (left, right, options) => samePath(left, right) ||
  strictlyWithin(left, right, options) || strictlyWithin(right, left, options);

export async function canonicalPath(target, { fs = defaultPathFs, pathApi: path = defaultPathApi } = {}) {
  if (typeof target !== 'string' || !path.isAbsolute(target) || path.normalize(target) !== target) {
    fail('An absolute, normalized worktree path is required.');
  }
  const remainder = [];
  let current = target;
  for (;;) {
    try {
      const real = await fs.realpath(current);
      return remainder.length ? path.join(real, ...remainder.reverse()) : real;
    } catch (error) {
      if (error.code !== 'ENOENT') fail(`Worktree path cannot be canonicalized (${error.code ?? 'unknown'}).`);
    }
    // realpath ENOENT on an existing entry is a dangling symlink: never guess its target.
    const dangling = await fs.lstat(current).then(() => true, (error) => {
      if (error.code === 'ENOENT') return false;
      fail(`Worktree path cannot be canonicalized (${error.code ?? 'unknown'}).`);
    });
    if (dangling) fail('Worktree path traverses a dangling symlink.');
    const parent = path.dirname(current);
    if (parent === current) fail('Worktree path has no existing ancestor.');
    remainder.push(path.basename(current));
    current = parent;
  }
}

async function gitIsDirectory(directory, fs, path) {
  return fs.lstat(path.join(directory, '.git')).then((stat) => stat.isDirectory(), (error) => {
    if (['ENOENT', 'ENOTDIR'].includes(error.code)) return false;
    fail(`Worktree .git cannot be inspected (${error.code ?? 'unknown'}).`);
  });
}

// The primary checkout behind a linked worktree's `<common>/worktrees/<name>` git directory.
export function mainCheckoutFromGitDirectory(gitDirectory) {
  if (typeof gitDirectory !== 'string' || !path.isAbsolute(gitDirectory)) return undefined;
  const worktrees = path.dirname(gitDirectory);
  const common = path.dirname(worktrees);
  return path.basename(worktrees) === 'worktrees' && path.basename(common) === '.git' ? path.dirname(common) : undefined;
}

// Canonical path of a worker (or role) worktree strictly inside the canonical
// root. Rejects symlink escapes, the root itself, main-checkout aliases (a
// `.git` directory at the target or any ancestor below the root) and overlap
// with any excluded checkout. Accepts the native or canonical spelling.
export async function resolveWorktreePath(root, target, { exclude = [], fs = defaultPathFs, pathApi: path = defaultPathApi } = {}) {
  if (typeof root !== 'string' || !path.isAbsolute(root)) fail('Configured worktree root required.');
  const options = { fs, pathApi: path };
  const canonicalRoot = await canonicalPath(path.normalize(root), options);
  const canonical = await canonicalPath(target, options);
  if (!strictlyWithin(canonicalRoot, canonical, options)) fail('Canonical worktree path escapes the configured Ralph worktree root.');
  for (const excluded of exclude.filter((entry) => typeof entry === 'string' && path.isAbsolute(entry))) {
    const other = await canonicalPath(path.normalize(excluded), options);
    if (overlaps(other, canonical, options)) fail('Worktree path aliases the main checkout or another Ralph checkout.');
  }
  for (let current = canonical; strictlyWithin(canonicalRoot, current, options); current = path.dirname(current)) {
    if (await gitIsDirectory(current, fs, path)) fail('Worktree path aliases a main checkout (its .git is a directory).');
  }
  return canonical;
}
