import { execFileSync } from 'node:child_process';
import {
  repository, ledgerBranch, requireThat, validateLedger, verifyTag, compareVersions, normalizeProtectionEvidence,
  hash, parseTag, publicLedgerQualification, publicRecord, validateRecord, requireKeys, requireString,
  validatePromotionOrigin, parseVersionFile, requireObject, validateReservationAdmission,
} from './release-policy.mjs';
import { publicAuthorization, writePublicSet } from './release-authorization.mjs';

// Schema 1 contains only these public maps, immutable references and scalar claims.
export const publicLedgerFields = [
  'schema', 'anchor', 'counter', 'lastHistoricalStable', 'reservations', 'identities', 'pointers', 'stages', 'qualifications',
];

function publicMap(value, project) {
  requireThat(value && typeof value === 'object' && !Array.isArray(value), 'Invalid public ledger map');
  return Object.fromEntries(Object.entries(value).map(([key, entry]) => [key, project(entry, key)]));
}

function publicReference(value, pattern) {
  requireThat(typeof value === 'string' && pattern.test(value), 'Invalid public ledger reference');
  return value;
}

const shaPattern = /^[a-f0-9]{40}$/;
const hashPattern = /^[a-f0-9]{64}$/;
const sequencePattern = /^[1-9][0-9]*$/;

function publicReservation(entry, key) {
  publicReference(key, hashPattern);
  const record = entry.record;
  const identitySha256 = publicReference(entry.identitySha256 || hash(record), hashPattern);
  const result = { admission: { ...entry.admission }, record: publicRecord(record, identitySha256), identitySha256 };
  if (entry.sequence !== undefined) result.sequence = publicReference(entry.sequence, sequencePattern);
  if (entry.tagObject !== undefined) result.tagObject = publicReference(entry.tagObject, shaPattern);
  if (entry.setHash !== undefined) result.setHash = publicReference(entry.setHash, hashPattern);
  if (entry.tagPublished !== undefined) {
    requireThat(typeof entry.tagPublished === 'boolean', 'Invalid public ledger tag claim');
    result.tagPublished = entry.tagPublished;
  }
  if (entry.set !== undefined) {
    result.set = writePublicSet(record, entry.set, identitySha256);
  }
  return result;
}

export function publicLedger(state) {
  validateLedger(state, state.anchor);
  const projectedState = {
    schema: 1,
    anchor: publicReference(state.anchor, shaPattern),
    counter: publicReference(state.counter, /^(0|[1-9][0-9]*)$/),
    reservations: publicMap(state.reservations, publicReservation),
    identities: publicMap(state.identities, (key, version) => {
      parseTag(`v${version}`);
      return publicReference(key, hashPattern);
    }),
    pointers: publicMap(state.pointers, (pointer, channel) => {
      requireThat(['stable', 'insider'].includes(channel), 'Invalid public ledger channel');
      const tag = parseTag(`v${pointer.canonicalVersion}`);
      requireThat(tag.channel === channel && pointer.releaseId === `${channel}:${tag.canonicalVersion}`,
        'Invalid public ledger pointer');
      requireThat(Object.keys(pointer).sort().join() ===
        ['releaseId', 'canonicalVersion', 'sourceCommit', 'setHash', 'allocationKey'].sort().join(),
      'Unknown public ledger pointer field');
      return {
        releaseId: pointer.releaseId, canonicalVersion: tag.canonicalVersion,
        sourceCommit: publicReference(pointer.sourceCommit, shaPattern),
        setHash: publicReference(pointer.setHash, hashPattern),
        allocationKey: publicReference(pointer.allocationKey, hashPattern),
      };
    }),
    stages: publicMap(state.stages ?? {}, (version, base) => {
      const tag = parseTag(`v${version}`);
      requireThat(tag.baseVersion === base && tag.channel === 'insider', 'Invalid public ledger stage');
      return tag.canonicalVersion;
    }),
    qualifications: publicMap(state.qualifications ?? {}, (qualification, sourceCommit) => {
      publicLedgerQualification(qualification, sourceCommit);
      return structuredClone(qualification);
    }),
  };
  if (state.lastHistoricalStable !== undefined) {
    requireThat(typeof state.lastHistoricalStable === 'string', 'Invalid public ledger stable floor');
    const stable = parseTag(`v${state.lastHistoricalStable}`);
    requireThat(stable.channel === 'stable', 'Invalid public ledger stable floor');
    projectedState.lastHistoricalStable = stable.canonicalVersion;
  }
  requireKeys(projectedState, publicLedgerFields.filter(field => field !== 'lastHistoricalStable'),
    ['lastHistoricalStable'], 'public ledger');
  validateLedger(projectedState, projectedState.anchor);
  return projectedState;
}

export function githubClient(token = process.env.GH_TOKEN) {
  requireThat(token, 'Missing GitHub credential');
  return async (endpoint, method = 'GET', body) => {
    const response = await fetch(`https://api.github.com/repos/${repository}/${endpoint}`, {
      method, headers: {
        Authorization: `Bearer ${token}`, Accept: 'application/vnd.github+json',
        'X-GitHub-Api-Version': '2022-11-28',
        ...(body ? { 'Content-Type': 'application/json' } : {}),
      }, body: body ? JSON.stringify(body) : undefined,
    });
    if (!response.ok) {
      const target = /^(rules\/|rulesets|environments\/)/.test(endpoint) ? 'protection policy' : endpoint;
      const error = new Error(`GitHub ${method} ${target}: HTTP ${response.status}`);
      error.status = response.status;
      throw error;
    }
    return response.status === 204 ? undefined : response.json();
  };
}

export async function branchHead(api, branch) {
  return (await api(`git/ref/heads/${branch}`)).object.sha;
}

export async function readVersion(api, sha) {
  const file = await api(`contents/VERSION?ref=${sha}`);
  requireThat(file.encoding === 'base64', 'VERSION response is not a file');
  return Buffer.from(file.content, 'base64').toString('utf8');
}

export async function readTag(api, tag) {
  let ref;
  try { ref = await api(`git/ref/tags/${tag}`); }
  catch (error) { if (error.status === 404) return undefined; throw error; }
  const object = ref.object.sha;
  let peeled = ref.object;
  for (let depth = 0; peeled.type === 'tag' && depth < 8; depth++) {
    peeled = (await api(`git/tags/${peeled.sha}`)).object;
  }
  requireThat(peeled.type === 'commit', 'Tag does not peel to commit');
  return { object, commit: peeled.sha };
}

export function gitLedger(api, anchor) {
  async function snapshot(revision) {
    const commit = await api(`git/commits/${revision}`);
    requireThat(Array.isArray(commit?.parents) && commit.parents.length === 1,
      'Ledger must have a linear single-parent history');
    requireString(commit.parents[0]?.sha, shaPattern, 'ledger parent commit');
    const tree = await api(`git/trees/${commit.tree.sha}`);
    requireThat(tree.truncated === false, 'Ledger tree is truncated');
    const entry = tree.tree.find(file => file.path === 'state.json' && file.type === 'blob');
    requireThat(entry, 'Ledger state is missing; owner recovery required');
    const blob = await api(`git/blobs/${entry.sha}`);
    requireThat(blob.encoding === 'base64', 'Unsupported ledger blob encoding');
    const state = JSON.parse(Buffer.from(blob.content, 'base64').toString('utf8'));
    requireKeys(state, publicLedgerFields.filter(field => field !== 'lastHistoricalStable'),
      ['lastHistoricalStable'], 'persisted public ledger');
    validateLedger(state, anchor);
    return { commit, state };
  }
  function validateContinuity(previous, state) {
    requireThat(Object.hasOwn(state, 'lastHistoricalStable') === Object.hasOwn(previous, 'lastHistoricalStable') &&
      state.lastHistoricalStable === previous.lastHistoricalStable,
    'Ledger lost or changed immutable historical stable floor');
    const counterDelta = BigInt(state.counter) - BigInt(previous.counter);
    requireThat(counterDelta >= 0n, 'Ledger counter rollback');
    requireThat(counterDelta <= 1n, 'Ledger counter jumped by more than one');
    const additions = Object.entries(state.reservations)
      .filter(([key]) => !Object.hasOwn(previous.reservations, key))
      .map(([, reservation]) => reservation);
    requireThat(additions.length <= 1, 'Ledger transaction added multiple reservations');
    for (const reservation of additions) validateReservationAdmission(previous, reservation.admission);
    if (counterDelta === 1n) {
      requireThat(additions.length === 1 && additions[0].record.channel === 'insider' &&
        additions[0].sequence === state.counter,
      'Ledger counter increment requires one matching insider reservation');
    } else {
      requireThat(additions.every(reservation => reservation.record.channel === 'stable'),
        'Ledger added insider reservation without advancing counter');
    }
    for (const [sourceCommit, qualification] of Object.entries(previous.qualifications)) {
      requireThat(JSON.stringify(state.qualifications[sourceCommit]) === JSON.stringify(qualification),
        'Ledger lost or changed an immutable qualification');
    }
    for (const [key, reservation] of Object.entries(previous.reservations)) {
      requireThat(JSON.stringify(state.reservations[key]?.record) === JSON.stringify(reservation.record) &&
        JSON.stringify(state.reservations[key]?.admission) === JSON.stringify(reservation.admission) &&
        state.reservations[key]?.identitySha256 === reservation.identitySha256,
        'Ledger lost or changed an immutable reservation');
      for (const field of ['tagObject', 'tagPublished', 'setHash', 'set']) {
        if (reservation[field] !== undefined) {
          requireThat(JSON.stringify(state.reservations[key]?.[field]) === JSON.stringify(reservation[field]),
            `Ledger lost or changed immutable ${field}`);
        }
      }
    }
    for (const [channel, pointer] of Object.entries(previous.pointers)) {
      const next = state.pointers[channel];
      requireThat(next && compareVersions(next.canonicalVersion, pointer.canonicalVersion) >= 0,
        'Ledger pointer rollback');
      if (next.canonicalVersion === pointer.canonicalVersion) {
        requireThat(JSON.stringify(next) === JSON.stringify(pointer), 'Ledger changed immutable pointer identity');
      }
    }
    for (const [base, stage] of Object.entries(previous.stages || {})) {
      requireThat(state.stages?.[base] && compareVersions(state.stages[base], stage) >= 0,
        'Ledger stage rollback');
    }
  }
  return {
    async read() {
      requireThat(/^[a-f0-9]{40}$/.test(anchor || ''), 'Owner must approve and pin RELEASE_LEDGER_ANCHOR');
      const revision = await branchHead(api, ledgerBranch);
      requireString(revision, shaPattern, 'ledger head commit');
      requireThat(revision !== anchor, 'Ledger seed is missing after the pinned anchor; owner recovery required');
      const ancestry = await api(`compare/${anchor}...${revision}`);
      requireThat(['ahead', 'identical'].includes(ancestry.status), 'Ledger anchor not in ancestry');
      let current = revision;
      let state;
      let child;
      const visited = new Set();
      while (current !== anchor) {
        requireThat(!visited.has(current), 'Ledger history contains a cycle');
        visited.add(current);
        const snapshotAtCommit = await snapshot(current);
        state ??= snapshotAtCommit.state;
        if (child) validateContinuity(snapshotAtCommit.state, child);
        child = snapshotAtCommit.state;
        current = snapshotAtCommit.commit.parents[0].sha;
      }
      // The pinned checkpoint precedes the seed: resolve it, but do not treat it as ledger state.
      const boundary = await api(`git/commits/${anchor}`);
      requireObject(boundary, 'ledger anchor commit');
      requireString(boundary.tree?.sha, shaPattern, 'ledger anchor tree');
      return { revision, state };
    },
    async compareAndSet(revision, state) {
      const content = JSON.stringify(publicLedger(state));
      const parent = await api(`git/commits/${revision}`);
      const blob = await api('git/blobs', 'POST', { content, encoding: 'utf-8' });
      const tree = await api('git/trees', 'POST', {
        base_tree: parent.tree.sha, tree: [{ path: 'state.json', mode: '100644', type: 'blob', sha: blob.sha }],
      });
      const commit = await api('git/commits', 'POST', {
        message: 'release: append allocation/authorization/complete-set transaction',
        tree: tree.sha, parents: [revision],
      });
      try {
        await api(`git/refs/heads/${ledgerBranch}`, 'PATCH', { sha: commit.sha, force: false });
        return true;
      } catch (error) {
        if (![409, 422].includes(error.status)) throw error;
        requireThat(await branchHead(api, ledgerBranch) !== revision,
          'Ledger update rejected by policy, not a CAS conflict');
        return false;
      }
    },
  };
}

export async function verifyProtection(api, channel, publisherAppId = process.env.RELEASE_PUBLISHER_APP_ID) {
  const readPolicy = async endpoint => {
    try { return await api(endpoint); }
    catch (error) {
      throw new Error(`Protection policy read failed${Number.isInteger(error.status) ? `: HTTP ${error.status}` : ''}`);
    }
  };
  const branch = channel === 'stable' ? 'main' : 'development';
  const branchRules = await readPolicy(`rules/branches/${branch}`);
  const environment = await readPolicy(`environments/release-${channel}`);
  const branchPolicies = await readPolicy(`environments/release-${channel}/deployment-branch-policies`);
  const allRulesets = await readPolicy('rulesets?per_page=100');
  requireThat(allRulesets.length < 100, 'Ruleset listing may be truncated');
  const rulesets = [];
  for (const name of ['release-canonical-tags', 'release-ledger-continuity',
    'release-tag-creators', 'release-ledger-writer']) {
    const summary = allRulesets.find(rule => rule.name === name && rule.enforcement === 'active');
    requireThat(summary, `Owner blocker: active ${name} ruleset missing`);
    const detail = await readPolicy(`rulesets/${summary.id}`);
    requireThat(detail.id === summary.id && detail.name === name, 'Ruleset identity changed during verification');
    rulesets.push(detail);
  }
  const evidence = { schema: 1, repository, channel, branch, publisherAppId,
    verifiedAt: new Date().toISOString(), branchRules, environment, branchPolicies, rulesets };
  return normalizeProtectionEvidence(evidence, channel, publisherAppId);
}

export async function ensureSourceTag(api, store, record, transact) {
  validateRecord(record);
  const preflight = state => {
    const projected = publicLedger(state);
    const entry = projected.reservations[record.allocationKey];
    requireThat(entry?.identitySha256 === hash(record) &&
      hash(entry.record) === hash(publicRecord(record)), 'Source tag authorization mismatch');
    return entry;
  };
  // Reserve the annotated object in the ledger BEFORE creating its public ref.
  await transact(store, async state => {
    preflight(state);
    const entry = state.reservations[record.allocationKey];
    if (entry.tagObject) return;
    requireThat(!await readTag(api, record.sourceTag), 'Existing tag has no immutable authorization');
    const tag = await api('git/tags', 'POST', {
      tag: record.sourceTag, object: record.sourceCommit, type: 'commit',
      message: JSON.stringify(publicAuthorization(record)),
      tagger: { name: 'PrintFarmer Release', email: 'release@users.noreply.github.com', date: record.created },
    });
    entry.tagObject = tag.sha;
  });
  const { state } = await store.read();
  const entry = preflight(state);
  const expected = entry.tagObject;
  const actual = await readTag(api, record.sourceTag);
  requireThat(!entry.tagPublished || actual,
    'Previously published tag was deleted; never recreate it');
  if (!actual) {
    await api('git/refs', 'POST', { ref: `refs/tags/${record.sourceTag}`, sha: expected });
  }
  verifyTag(record, expected, await readTag(api, record.sourceTag));
  await transact(store, state => { state.reservations[record.allocationKey].tagPublished = true; });
}

export async function verifyStableQualification(api, state, admission) {
  if (admission.channel !== 'stable') return undefined;
  publicLedger(state);
  const qualification = publicLedgerQualification(state.qualifications[admission.sourceCommit], admission.sourceCommit);
  if (qualification.mode === 'hotfix') return qualification;
  const candidate = validatePromotionOrigin(state, qualification);
  requireThat(candidate.record.baseVersion === admission.baseVersion, 'Promotion target differs from qualified insider base');
  const readTree = async commitSha => {
    const commit = await api(`git/commits/${commitSha}`);
    requireThat(commit?.sha === commitSha, 'Promotion commit response mismatch');
    requireString(commit.tree?.sha, shaPattern, 'promotion commit tree');
    const tree = await api(`git/trees/${commit.tree.sha}?recursive=1`);
    requireThat(tree?.sha === commit.tree.sha && tree.truncated === false && Array.isArray(tree.tree),
      'Promotion tree missing or truncated');
    const entries = new Map();
    const paths = new Set();
    for (const entry of tree.tree) {
      requireObject(entry, 'promotion tree entry');
      requireString(entry.path, /^[^\u0000-\u001f\u007f]+$/, 'promotion tree path');
      requireString(entry.sha, shaPattern, 'promotion tree object');
      requireThat(!paths.has(entry.path) &&
        ((entry.type === 'tree' && entry.mode === '040000') ||
         (entry.type === 'blob' && ['100644', '100755', '120000'].includes(entry.mode)) ||
         (entry.type === 'commit' && entry.mode === '160000')), 'Invalid promotion tree entry');
      paths.add(entry.path);
      entries.set(entry.path, { mode: entry.mode, type: entry.type,
        ...(entry.type === 'tree' ? {} : { sha: entry.sha }) });
    }
    requireThat(entries.get('VERSION')?.mode === '100644', 'Missing regular VERSION metadata in promotion tree');
    return { sha: tree.sha, entries };
  };
  const origin = await readTree(candidate.record.sourceCommit);
  const source = await readTree(admission.sourceCommit);
  const metadataChanges = [];
  for (const path of [...new Set([...origin.entries.keys(), ...source.entries.keys()])].sort()) {
    const before = origin.entries.get(path);
    const after = source.entries.get(path);
    if (JSON.stringify(before) === JSON.stringify(after)) continue;
    requireThat(path === 'VERSION' && before?.type === 'blob' && after?.type === 'blob' &&
      before.mode === '100644' && after.mode === '100644',
    'Unqualified tree change: promotion permits only VERSION metadata');
    metadataChanges.push({ path, before: before.sha, after: after.sha });
  }
  for (const commit of [candidate.record.sourceCommit, admission.sourceCommit]) {
    requireThat(parseVersionFile(await readVersion(api, commit)) === admission.baseVersion,
      'Promotion VERSION differs from qualified base');
  }
  const evidence = { schema: 1, originTree: origin.sha, sourceTree: source.sha, metadataChanges };
  requireThat(hash(qualification.treeEvidence) === hash({ ...evidence, diffSha256: hash(evidence) }),
    'Promotion tree evidence differs from reproducible Git comparison');
  return qualification;
}

export function command(file, args) {
  return execFileSync(file, args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
}
