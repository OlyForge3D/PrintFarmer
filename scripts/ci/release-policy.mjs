import { createHash } from 'node:crypto';
import { publicIdentityFields } from '../../src/Web/ReactApp/public-release-identity.mjs';

export const repository = 'OlyForge3D/PrintFarmer';
export const workflow = '.github/workflows/consolidated-release.yml';
export const ledgerBranch = 'release-ledger';
const numeric = '(0|[1-9][0-9]*)';
const basePattern = `${numeric}\\.${numeric}\\.${numeric}`;
const tagPattern = new RegExp(`^v(${basePattern})(?:-(insider|beta|rc)\\.([1-9][0-9]*))?$`);
const shaPattern = /^[a-f0-9]{40}$/;
const hashPattern = /^[a-f0-9]{64}$/;
const positivePattern = /^[1-9][0-9]*$/;
export const components = {
  api: ['linux/amd64', 'linux/arm64'],
  frontend: ['linux/amd64', 'linux/arm64'],
  'slicer-host': ['linux/amd64', 'linux/arm64'],
  'printer-discovery': ['linux/amd64', 'linux/arm64'],
  'orcaslicer-worker': ['linux/amd64'],
  monolith: ['linux/amd64', 'linux/arm64'],
};

export class ReleasePolicyError extends Error {
  name = 'ReleasePolicyError';
}

export function requireThat(condition, message) {
  if (!condition) throw new ReleasePolicyError(message);
}

export function requireObject(value, description) {
  requireThat(value && typeof value === 'object' && !Array.isArray(value), `Invalid ${description} object`);
}

export function requireKeys(value, required, optional = [], description = 'release schema') {
  requireObject(value, description);
  requireThat(required.every(key => Object.hasOwn(value, key)) &&
    Object.keys(value).every(key => [...required, ...optional].includes(key) && value[key] !== undefined),
  `Invalid ${description} fields`);
}

export function requireString(value, pattern, description) {
  requireThat(typeof value === 'string' && !/[\r\n]/.test(value) && pattern.test(value), `Invalid ${description}`);
}

export function requireTimestamp(value, description) {
  requireThat(typeof value === 'string' && /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$/.test(value) &&
    Number.isFinite(Date.parse(value)) && new Date(value).toISOString() === value, `Invalid ${description}`);
}

export function parseTag(tag) {
  const match = typeof tag === 'string' && tag.match(tagPattern);
  requireThat(match && match[0] === tag, 'Invalid canonical tag (strict SemVer, positive N, no leading zeros)');
  const [, baseVersion, major, minor, patch, stage, sequence] = match;
  return { baseVersion, major, minor, patch, stage, sequence,
    canonicalVersion: tag.slice(1), channel: stage ? 'insider' : 'stable' };
}

export function parseVersionFile(text) {
  // Permit the text-file final newline, not arbitrary whitespace normalization.
  requireThat(typeof text === 'string', 'Invalid VERSION content');
  const tag = text.replace(/\r?\n$/, '');
  const { major, minor, patch, stage } = parseTag(tag);
  requireThat(!stage, 'VERSION must contain only vX.Y.Z');
  return `${BigInt(major)}.${BigInt(minor)}.${BigInt(patch)}`;
}

export function compareVersions(left, right) {
  const a = parseTag(`v${left}`);
  const b = parseTag(`v${right}`);
  for (const field of ['major', 'minor', 'patch']) {
    if (BigInt(a[field]) !== BigInt(b[field])) return BigInt(a[field]) > BigInt(b[field]) ? 1 : -1;
  }
  // SemVer orders beta < insider < rc; stage transitions must follow this order.
  if (a.stage !== b.stage) {
    if (!a.stage) return 1;
    if (!b.stage) return -1;
    return a.stage > b.stage ? 1 : -1;
  }
  return !a.stage || a.sequence === b.sequence ? 0 : BigInt(a.sequence) > BigInt(b.sequence) ? 1 : -1;
}

export function hash(value) {
  return createHash('sha256').update(JSON.stringify(value)).digest('hex');
}

export function admit(context, selectedHead, versionText, lastStable) {
  requireObject(context, 'release admission context');
  requireThat(context.repository === repository, 'Untrusted repository');
  requireThat(['workflow_dispatch', 'schedule'].includes(context.event), 'Event cannot authorize publication');
  requireThat(context.workflowIdentity === `${repository}/${workflow}@refs/heads/${context.workflowBranch}`,
    'Untrusted workflow/caller identity');
  requireThat(context.workflowSha === context.eventSha, 'Workflow SHA mismatch');
  const channel = context.event === 'schedule' ? 'insider' : context.channel;
  const sourceBranch = channel === 'stable' ? 'main' : 'development';
  requireThat(['stable', 'insider'].includes(channel), 'Invalid channel');
  requireThat(context.ref === `refs/heads/${sourceBranch}`, 'Branch/channel mismatch');
  requireThat(context.workflowBranch === sourceBranch, 'Workflow/source branch mismatch');
  requireThat(shaPattern.test(selectedHead) && context.eventSha === selectedHead,
    'Source must equal canonical branch HEAD at admission; reselect and requalify');
  for (const field of ['buildId', 'buildAttempt']) {
    requireString(context[field], positivePattern, field);
  }
  const baseVersion = parseVersionFile(versionText);
  const stage = channel === 'stable' ? undefined : (context.stage || 'insider');
  requireThat(!stage || ['insider', 'beta', 'rc'].includes(stage), 'Unsupported stage');
  requireThat(channel !== 'stable' || !context.stage, 'Stable cannot select a stage');
  requireThat(context.event !== 'schedule' || stage === 'insider', 'Schedule only supports ordinary insider');
  if (channel === 'insider' && lastStable) {
    requireThat(compareVersions(baseVersion, lastStable) > 0, 'Development base must exceed published stable');
  }
  if (context.requestedTag) {
    const requested = parseTag(context.requestedTag);
    requireThat(requested.baseVersion === baseVersion && requested.channel === channel && requested.stage === stage,
      'Requested tag disagrees with VERSION/channel/stage');
  }
  return {
    repository, channel, baseVersion, sourceBranch, ...(stage ? { stage } : {}),
    sourceCommit: selectedHead, authorizedBranchHead: selectedHead,
    buildId: context.buildId, buildAttempt: context.buildAttempt,
    workflowIdentity: context.workflowIdentity, workflowCommit: context.workflowSha,
  };
}

export function allocationKey(admission) {
  return hash([admission.repository, admission.workflowIdentity, admission.buildId,
    admission.buildAttempt, admission.sourceCommit, admission.baseVersion]);
}

const admissionFields = [
  'repository', 'channel', 'baseVersion', 'sourceBranch', 'sourceCommit', 'authorizedBranchHead',
  'buildId', 'buildAttempt', 'workflowIdentity', 'workflowCommit',
];

function channelFields(record) {
  return record?.channel === 'insider' ? ['stage'] : [];
}

export function validateAdmission(admission) {
  requireKeys(admission, [...admissionFields, ...channelFields(admission)], [], 'ledger admission');
  requireThat(admission.repository === repository && ['stable', 'insider'].includes(admission.channel),
    'Invalid ledger admission repository/channel');
  requireThat(parseTag(`v${admission.baseVersion}`).channel === 'stable', 'Invalid ledger admission base version');
  const branch = admission.channel === 'stable' ? 'main' : 'development';
  requireThat(admission.sourceBranch === branch &&
    admission.workflowIdentity === `${repository}/${workflow}@refs/heads/${branch}`,
  'Invalid ledger admission workflow/branch');
  requireString(admission.sourceCommit, shaPattern, 'ledger admission source commit');
  requireThat(admission.authorizedBranchHead === admission.sourceCommit &&
    admission.workflowCommit === admission.sourceCommit, 'Invalid ledger admission commit binding');
  for (const field of ['buildId', 'buildAttempt']) requireString(admission[field], positivePattern, `ledger admission ${field}`);
  requireThat(admission.channel === 'stable' || ['insider', 'beta', 'rc'].includes(admission.stage),
    'Invalid ledger admission stage');
  return admission;
}

function recordAdmission(record) {
  return Object.fromEntries([...admissionFields, ...channelFields(record)].map(field => [field, record[field]]));
}

export function validateRecord(record, projected = false) {
  const fields = [
    'schema', ...admissionFields, ...channelFields(record), 'releaseId', 'canonicalVersion',
    'sourceTag', 'allocationKey', 'created', ...(record?.channel === 'insider' ? ['sequence'] : []),
    ...(projected ? ['buildTime', 'identitySha256'] : ['protection', ...(record?.channel === 'stable' ? ['qualification'] : [])]),
  ];
  requireKeys(record, fields, [], projected ? 'projected ledger record' : 'authorization');
  requireThat(record.schema === 1, 'Invalid ledger identity schema');
  validateAdmission(recordAdmission(record));
  const tag = parseTag(record.sourceTag);
  requireThat(record.canonicalVersion === tag.canonicalVersion && record.baseVersion === tag.baseVersion &&
    record.channel === tag.channel && record.stage === tag.stage && record.sequence === tag.sequence &&
    record.releaseId === `${tag.channel}:${tag.canonicalVersion}` &&
    record.allocationKey === allocationKey(record), 'Invalid canonical record identity');
  requireTimestamp(record.created, 'authorization timestamp');
  if (projected) {
    requireThat(record.buildTime === record.created, 'Invalid projected record timestamp binding');
    requireString(record.identitySha256, hashPattern, 'ledger authorization hash');
  } else {
    verifyProtectionEvidence(record.protection, record.channel);
    requireThat(Date.parse(record.protection.verifiedAt) <= Date.parse(record.created),
      'Protection attestation postdates authorization');
    if (record.channel === 'stable') publicLedgerQualification(record.qualification, record.sourceCommit);
  }
  return record;
}

export function publicAuthorization(record) {
  requireObject(record, 'public identity');
  const identity = {
    ...Object.fromEntries(publicIdentityFields.map(field => [field, record[field]])),
    buildTime: Object.hasOwn(record, 'created') ? record.created : record.buildTime,
    identitySha256: Object.hasOwn(record, 'identitySha256') ? record.identitySha256 : hash(record),
  };
  validatePublicAuthorization(identity);
  return identity;
}

export function validatePublicAuthorization(identity) {
  requireKeys(identity, [...publicIdentityFields, 'buildTime', 'identitySha256'], [], 'public authorization');
  const tag = parseTag(identity.sourceTag);
  requireThat(identity.canonicalVersion === tag.canonicalVersion && identity.baseVersion === tag.baseVersion &&
    identity.channel === tag.channel && identity.releaseId === `${tag.channel}:${tag.canonicalVersion}`,
  'Invalid public authorization canonical identity');
  validateAdmission({
    repository, channel: identity.channel, baseVersion: identity.baseVersion, sourceBranch: identity.sourceBranch,
    sourceCommit: identity.sourceCommit, authorizedBranchHead: identity.authorizedBranchHead,
    buildId: identity.buildId, buildAttempt: identity.buildAttempt, workflowIdentity: identity.workflowIdentity,
    workflowCommit: identity.sourceCommit, ...(tag.stage ? { stage: tag.stage } : {}),
  });
  requireTimestamp(identity.buildTime, 'authorization timestamp');
  requireString(identity.identitySha256, hashPattern, 'public authorization identity hash');
}

export function publicRecord(record, identitySha256) {
  requireObject(record, 'ledger record');
  validateRecord(record, Object.hasOwn(record, 'identitySha256'));
  if (identitySha256 === undefined) identitySha256 = Object.hasOwn(record, 'identitySha256') ? record.identitySha256 : hash(record);
  requireString(identitySha256, hashPattern, 'ledger authorization hash');
  return {
    ...publicAuthorization(record), identitySha256, schema: 1, repository,
    workflowCommit: record.workflowCommit, allocationKey: record.allocationKey, created: record.created,
    ...(record.channel === 'insider' ? { stage: record.stage, sequence: record.sequence } : {}),
  };
}

function publicDigest(value) {
  requireString(value, /^sha256:[a-f0-9]{64}$/, 'public set digest');
  return value;
}

export function writePublicSet(record, set, identitySha256) {
  requireObject(record, 'public set identity');
  if (identitySha256 === undefined) identitySha256 = Object.hasOwn(record, 'identitySha256') ? record.identitySha256 : hash(record);
  requireString(identitySha256, hashPattern, 'public set identity hash');
  const identity = { ...publicAuthorization(record), identitySha256 };
  requireObject(set, 'public set');
  requireThat(set.schema === 1 && set.managedEligible === false, 'Invalid public set schema/eligibility');
  requireObject(set.identity, 'public set identity');
  requireThat(hash(set.identity) === hash(identity) ||
    (hash(set.identity) === identitySha256 && hash(publicAuthorization(set.identity)) === hash(identity)),
  'Public set identity mismatch');
  const labels = {
    ...identityLabels({ ...identity, repository, created: identity.buildTime }),
    'org.printfarmer.identity-sha256': identitySha256,
  };
  requireKeys(set.images, Object.keys(components), [], 'public set component');
  const images = {};
  for (const [name, expectedPlatforms] of Object.entries(components)) {
    const image = set.images[name];
    requireObject(image, 'public set image');
    const digest = publicDigest(image.digest);
    requireKeys(image.platforms, expectedPlatforms, [], 'public set platform');
    const platforms = {};
    for (const platform of expectedPlatforms) {
      const value = image.platforms[platform];
      requireObject(value, 'public set platform');
      requireObject(value.labels, 'public set labels');
      for (const [key, expected] of Object.entries(labels)) {
        requireThat(Object.hasOwn(value.labels, key) && value.labels[key] === expected, 'Invalid public set identity label');
      }
      platforms[platform] = { digest: publicDigest(value.digest), labels: { ...labels } };
    }
    images[name] = { digest, platforms };
  }
  return { schema: 1, identity, managedEligible: false, images };
}

export function validateApprovalMode(mode) {
  requireThat(['single-maintainer', 'separation-of-duties'].includes(mode),
    'Owner blocker: RELEASE_APPROVAL_MODE must be single-maintainer or separation-of-duties');
  return mode;
}

function approvedReviewers(value) {
  if (value === undefined || value === '') return ['jpapiez'];
  let reviewers;
  try { reviewers = JSON.parse(value); } catch {
    throw new ReleasePolicyError('Owner blocker: invalid owner-approved reviewer configuration');
  }
  requireThat(Array.isArray(reviewers) && reviewers.length > 0 && reviewers.every(login =>
    typeof login === 'string' && /^[a-z\d](?:[a-z\d-]{0,37}[a-z\d])?$/i.test(login) && !/[\r\n]/.test(login)),
  'Owner blocker: invalid owner-approved reviewer configuration');
  return ['jpapiez', ...reviewers.map(login => login.toLowerCase())];
}

function eligibleReviewer(entry) {
  return entry && ['User', 'Team'].includes(entry.type) &&
    Number.isSafeInteger(entry.reviewer?.id) && entry.reviewer.id > 0 &&
    typeof entry.reviewer[entry.type === 'User' ? 'login' : 'slug'] === 'string' &&
    entry.reviewer[entry.type === 'User' ? 'login' : 'slug'].length > 0;
}

export function verifyRawProtectionEvidence(evidence, channel, publisherAppId, approvalMode, ownerApprovedReviewers) {
  validateApprovalMode(approvalMode);
  const branch = channel === 'stable' ? 'main' : 'development';
  requireThat(evidence?.schema === 1 && evidence.repository === repository &&
    ['stable', 'insider'].includes(channel) && evidence.channel === channel && evidence.branch === branch &&
    /^[1-9][0-9]*$/.test(publisherAppId || '') && evidence.publisherAppId === publisherAppId &&
    Number.isFinite(Date.parse(evidence.verifiedAt)),
  'Missing or mismatched publisher protection evidence');
  const { branchRules: rules, environment, branchPolicies: policies, rulesets } = evidence;
  requireThat(Array.isArray(rules) && Array.isArray(rulesets) && rulesets.length === 4,
    'Incomplete protection evidence');
  for (const type of ['deletion', 'non_fast_forward', 'pull_request', 'required_status_checks']) {
    requireThat(rules.some(rule => rule.type === type), `Owner blocker: ${branch} lacks active ${type} rule`);
  }
  requireThat(rules.some(rule => rule.type === 'pull_request' &&
    rule.parameters?.require_code_owner_review && rule.parameters?.required_approving_review_count >= 1),
  'Owner blocker: workflow/VERSION code-owner review not enforced');
  requireThat(rules.some(rule => rule.type === 'required_status_checks' &&
    rule.parameters?.required_status_checks?.length > 0),
  'Owner blocker: no exact-SHA required checks');
  requireThat(environment?.name === `release-${channel}` &&
    environment.deployment_branch_policy?.custom_branch_policies,
  'Owner blocker: publishing environment lacks branch restrictions');
  requireThat(environment.can_admins_bypass === false,
    'Owner blocker: publishing environment must explicitly disable administrator bypass');
  requireThat(policies?.branch_policies?.length === 1 &&
    policies.branch_policies[0].name === branch && policies.branch_policies[0].type === 'branch',
  'Owner blocker: publishing environment must allow only its canonical branch');
  const reviewerRules = Array.isArray(environment.protection_rules)
    ? environment.protection_rules.filter(rule => rule.type === 'required_reviewers') : [];
  requireThat(reviewerRules.length === 1 && Array.isArray(reviewerRules[0].reviewers) &&
    reviewerRules[0].reviewers.length > 0 && reviewerRules[0].reviewers.every(eligibleReviewer),
  'Owner blocker: publishing environment requires manual approval by eligible reviewers');
  const reviewerRule = reviewerRules[0];
  requireThat(reviewerRule.prevent_self_review === (approvalMode === 'separation-of-duties'),
    'Owner blocker: publishing environment self-review setting conflicts with approval mode');
  if (approvalMode === 'single-maintainer') {
    const approved = approvedReviewers(ownerApprovedReviewers);
    // GitHub accepts any one listed reviewer, so every possible approver must be owner-approved.
    requireThat(reviewerRule.reviewers.every(entry => entry.type === 'User' &&
      approved.includes(entry.reviewer.login.toLowerCase())),
    'Owner blocker: publishing environment reviewers must be explicitly owner-approved users');
  }
  for (const name of ['release-canonical-tags', 'release-ledger-continuity',
    'release-tag-creators', 'release-ledger-writer']) {
    const rule = rulesets.find(item => item.name === name && item.enforcement === 'active');
    requireThat(rule, `Owner blocker: active ${name} ruleset missing`);
    const tag = name === 'release-canonical-tags' || name === 'release-tag-creators';
    requireThat(rule.target === (tag ? 'tag' : 'branch') &&
      rule.conditions?.ref_name?.include?.includes(tag ? 'refs/tags/v*' : `refs/heads/${ledgerBranch}`) &&
      rule.conditions?.ref_name?.exclude?.length === 0,
    `Owner blocker: ${name} has incorrect scope`);
    const continuity = name === 'release-canonical-tags' || name === 'release-ledger-continuity';
    if (continuity) {
      requireThat(rule.bypass_actors?.length === 0, `Owner blocker: ${name} permits continuity bypass`);
      for (const type of tag ? ['update', 'deletion'] : ['non_fast_forward', 'deletion']) {
        requireThat(rule.rules?.some(item => item.type === type), `Owner blocker: ${name} lacks ${type}`);
      }
    } else {
      requireThat(rule.rules?.some(item => item.type === (tag ? 'creation' : 'update')) &&
        rule.bypass_actors?.length === 1 && rule.bypass_actors[0].actor_type === 'Integration' &&
        String(rule.bypass_actors[0].actor_id) === publisherAppId,
      `Owner blocker: ${name} must restrict writes to one explicitly approved publisher app`);
    }
  }
}

const protectionProfile = 'printfarmer-release-protection/v3';
const protectionClaims = [
  'branchDeletionBlocked', 'branchRewritesBlocked', 'codeOwnerApprovalRequired',
  'requiredChecksEnforced', 'canonicalEnvironmentBranchOnly', 'manualApprovalRequired',
  'environmentAdminBypassBlocked',
  'canonicalTagsImmutable', 'ledgerContinuityProtected', 'exclusiveApprovedPublisher',
];

function approvalAssurance(mode) {
  return mode === 'single-maintainer' ? 'owner-confirmed/self-attested' : 'non-self-review-enforced';
}

export function normalizeProtectionEvidence(evidence, channel, publisherAppId, approvalMode, ownerApprovedReviewers) {
  verifyRawProtectionEvidence(evidence, channel, publisherAppId, approvalMode, ownerApprovedReviewers);
  // Digest only public claims, never low-entropy actor IDs or raw API payloads.
  const attestation = {
    schema: 4, repository, channel, branch: evidence.branch,
    verifiedAt: evidence.verifiedAt, policyProfile: protectionProfile,
    approvalMode, approvalAssurance: approvalAssurance(approvalMode),
    claims: { ...Object.fromEntries(protectionClaims.map(claim => [claim, true])),
      nonSelfApprovalRequired: approvalMode === 'separation-of-duties' },
  };
  return { ...attestation, policyDigest: hash(attestation) };
}

export function verifyProtectionEvidence(evidence, channel) {
  const fields = ['schema', 'repository', 'channel', 'branch', 'verifiedAt', 'policyProfile',
    'approvalMode', 'approvalAssurance', 'claims', 'policyDigest'];
  requireThat(evidence && Object.keys(evidence).sort().join() === fields.sort().join() &&
    evidence.schema === 4 && evidence.repository === repository &&
    ['stable', 'insider'].includes(channel) && evidence.channel === channel &&
    evidence.branch === (channel === 'stable' ? 'main' : 'development') &&
    evidence.policyProfile === protectionProfile &&
    typeof evidence.verifiedAt === 'string' && Number.isFinite(Date.parse(evidence.verifiedAt)) &&
    new Date(evidence.verifiedAt).toISOString() === evidence.verifiedAt,
  'Missing or mismatched normalized protection attestation');
  validateApprovalMode(evidence.approvalMode);
  requireThat(evidence.approvalAssurance === approvalAssurance(evidence.approvalMode),
    'Invalid normalized approval assurance');
  requireThat(evidence.claims && Object.keys(evidence.claims).sort().join() ===
    [...protectionClaims, 'nonSelfApprovalRequired'].sort().join() &&
    protectionClaims.every(claim => evidence.claims[claim] === true) &&
    evidence.claims.nonSelfApprovalRequired === (evidence.approvalMode === 'separation-of-duties'),
  'Required normalized protection claims missing or weakened');
  const canonical = {
    schema: evidence.schema, repository, channel, branch: evidence.branch,
    verifiedAt: evidence.verifiedAt, policyProfile: protectionProfile,
    approvalMode: evidence.approvalMode, approvalAssurance: evidence.approvalAssurance,
    claims: { ...Object.fromEntries(protectionClaims.map(claim => [claim, evidence.claims[claim]])),
      nonSelfApprovalRequired: evidence.claims.nonSelfApprovalRequired },
  };
  requireThat(evidence.policyDigest === hash(canonical), 'Normalized protection digest mismatch');
}

export function validateLedger(state, anchor) {
  requireThat(state?.schema === 1 && typeof anchor === 'string' && !/[\r\n]/.test(anchor) &&
    shaPattern.test(anchor) && state.anchor === anchor,
    'Ledger missing or continuity anchor mismatch: owner recovery required');
  requireString(state.counter, /^(0|[1-9][0-9]*)$/, 'ledger counter');
  for (const field of ['reservations', 'identities', 'pointers', 'stages', 'qualifications']) {
    requireObject(state[field], `public ledger ${field}`);
  }
  if (Object.hasOwn(state, 'lastHistoricalStable')) {
    requireThat(typeof state.lastHistoricalStable === 'string' &&
      parseTag(`v${state.lastHistoricalStable}`).channel === 'stable', 'Invalid public ledger stable floor');
  }
  for (const [version, key] of Object.entries(state.identities)) {
    parseTag(`v${version}`);
    requireString(key, hashPattern, 'ledger identity reference');
    requireThat(state.reservations[key]?.record?.canonicalVersion === version,
      'Ledger identity continuity violation');
  }
  const sequences = new Set();
  for (const [key, reservation] of Object.entries(state.reservations)) {
    requireString(key, hashPattern, 'ledger allocation key');
    requireObject(reservation, 'public ledger reservation');
    const projected = Object.hasOwn(reservation, 'identitySha256') || Object.hasOwn(reservation.record ?? {}, 'identitySha256');
    requireKeys(reservation, ['admission', 'record',
      ...(projected ? ['identitySha256'] : []),
      ...(reservation.record?.channel === 'insider' ? ['sequence'] : [])],
    ['tagObject', 'tagPublished', 'setHash', 'set'], 'public ledger reservation');
    validateRecord(reservation.record, projected);
    validateAdmission(reservation.admission);
    requireThat(Object.entries(reservation.admission).every(([field, value]) => reservation.record[field] === value),
      'Ledger admission/record mismatch');
    if (projected) requireThat(reservation.record.identitySha256 === reservation.identitySha256,
      'Ledger authorization hash mismatch');
    requireThat(reservation.record.allocationKey === key &&
      state.identities[reservation.record.canonicalVersion] === key &&
      reservation.sequence === reservation.record.sequence, 'Ledger identity continuity violation');
    if (Object.hasOwn(reservation, 'tagObject')) requireString(reservation.tagObject, shaPattern, 'public ledger tag object');
    if (Object.hasOwn(reservation, 'tagPublished')) requireThat(reservation.tagPublished === true &&
      reservation.tagObject, 'Invalid public ledger tag publication claim');
    requireThat(Object.hasOwn(reservation, 'setHash') === Object.hasOwn(reservation, 'set'),
      'Incomplete public ledger set/hash');
    if (Object.hasOwn(reservation, 'set')) {
      requireString(reservation.setHash, hashPattern, 'public ledger set hash');
      requireThat(reservation.setHash === hash(writePublicSet(reservation.record, reservation.set,
        reservation.identitySha256 ?? hash(reservation.record))), 'Public set hash mismatch');
    }
    if (reservation.sequence) {
      requireThat(BigInt(reservation.sequence) <= BigInt(state.counter) &&
        !sequences.has(reservation.sequence), 'Ledger sequence continuity violation');
      sequences.add(reservation.sequence);
      requireThat(state.stages[reservation.record.baseVersion] &&
        compareVersions(state.stages[reservation.record.baseVersion], reservation.record.canonicalVersion) >= 0,
      'Ledger stage continuity violation');
    }
    if (reservation.record.channel === 'stable') {
      const qualification = state.qualifications[reservation.record.sourceCommit];
      publicLedgerQualification(qualification, reservation.record.sourceCommit);
      if (qualification.mode === 'promotion') requireThat(
        validatePromotionOrigin(state, qualification).record.baseVersion === reservation.record.baseVersion,
        'Promotion target differs from qualified insider base');
      if (!projected) requireThat(
        hash(publicLedgerQualification(qualification, reservation.record.sourceCommit)) ===
        hash(publicLedgerQualification(reservation.record.qualification, reservation.record.sourceCommit)),
        'Ledger stable qualification mismatch');
    }
  }
  for (const [base, version] of Object.entries(state.stages)) {
    const tag = parseTag(`v${version}`);
    requireThat(tag.channel === 'insider' && tag.baseVersion === base && state.identities[version],
      'Invalid public ledger stage');
  }
  for (const [channel, pointer] of Object.entries(state.pointers)) {
    requireKeys(pointer, ['releaseId', 'canonicalVersion', 'sourceCommit', 'setHash', 'allocationKey'],
      [], 'public ledger pointer');
    requireThat(['stable', 'insider'].includes(channel), 'Invalid public ledger pointer channel');
    const reservation = state.reservations[pointer.allocationKey];
    requireThat(reservation?.set && reservation.record.channel === channel &&
      pointer.setHash === reservation.setHash && pointer.releaseId === reservation.record.releaseId &&
      pointer.canonicalVersion === reservation.record.canonicalVersion &&
      pointer.sourceCommit === reservation.record.sourceCommit, 'Invalid public ledger pointer binding');
  }
  for (const [sourceCommit, qualification] of Object.entries(state.qualifications)) {
    publicLedgerQualification(qualification, sourceCommit);
    if (qualification.mode === 'promotion') validatePromotionOrigin(state, qualification);
  }
}

export function publicLedgerQualification(qualification, sourceCommit) {
  const claims = ['reviewed', 'tests', 'compatibility', 'migrations', 'recovery'];
  const fields = ['schema', 'sourceCommit', ...claims, 'mode',
    ...(qualification?.mode === 'promotion' ? ['promotionOrigin', 'treeEvidence'] : ['reasonSha256'])];
  requireString(sourceCommit, shaPattern, 'public ledger qualification source');
  requireThat(qualification && !Array.isArray(qualification) &&
    Object.keys(qualification).sort().join() === fields.sort().join() &&
    qualification.schema === 1 && typeof sourceCommit === 'string' && shaPattern.test(sourceCommit) &&
    qualification.sourceCommit === sourceCommit && claims.every(field => qualification[field] === true) &&
    ['promotion', 'hotfix'].includes(qualification.mode), 'Invalid public ledger qualification');
  const result = {
    schema: 1, sourceCommit, ...Object.fromEntries(claims.map(field => [field, true])), mode: qualification.mode,
  };
  if (qualification.mode === 'promotion') {
    const origin = qualification.promotionOrigin;
    requireKeys(origin, ['allocationKey', 'releaseId', 'sourceCommit', 'setHash'], [], 'public ledger promotion qualification');
    for (const field of ['allocationKey', 'setHash']) requireString(origin[field], hashPattern, 'public ledger promotion qualification hash');
    requireString(origin.sourceCommit, shaPattern, 'public ledger promotion qualification source');
    requireThat(origin && !Array.isArray(origin) &&
      Object.keys(origin).sort().join() === ['allocationKey', 'releaseId', 'sourceCommit', 'setHash'].sort().join() &&
      typeof origin.allocationKey === 'string' && /^[a-f0-9]{64}$/.test(origin.allocationKey) &&
      typeof origin.setHash === 'string' && /^[a-f0-9]{64}$/.test(origin.setHash) &&
      typeof origin.sourceCommit === 'string' && shaPattern.test(origin.sourceCommit) &&
      typeof origin.releaseId === 'string' && origin.releaseId.startsWith('insider:') &&
      parseTag(`v${origin.releaseId.slice('insider:'.length)}`).channel === 'insider',
    'Invalid public ledger promotion qualification');
    result.promotionOrigin = {
      allocationKey: origin.allocationKey, releaseId: origin.releaseId,
      sourceCommit: origin.sourceCommit, setHash: origin.setHash,
    };
    const evidence = qualification.treeEvidence;
    requireKeys(evidence, ['schema', 'originTree', 'sourceTree', 'metadataChanges', 'diffSha256'],
      [], 'public ledger promotion tree evidence');
    requireThat(evidence.schema === 1, 'Invalid promotion tree evidence schema');
    for (const field of ['originTree', 'sourceTree']) requireString(evidence[field], shaPattern, 'promotion tree reference');
    requireThat(Array.isArray(evidence.metadataChanges) && evidence.metadataChanges.length <= 1,
      'Invalid promotion metadata changes');
    for (const change of evidence.metadataChanges) {
      requireKeys(change, ['path', 'before', 'after'], [], 'promotion metadata change');
      requireThat(change.path === 'VERSION' && change.before !== change.after, 'Invalid promotion metadata path/change');
      for (const field of ['before', 'after']) requireString(change[field], shaPattern, 'promotion metadata blob');
    }
    const payload = { schema: 1, originTree: evidence.originTree, sourceTree: evidence.sourceTree,
      metadataChanges: evidence.metadataChanges.map(change => ({
        path: change.path, before: change.before, after: change.after,
      })) };
    requireThat(evidence.diffSha256 === hash(payload), 'Invalid promotion tree evidence digest');
    result.treeEvidence = { ...payload, diffSha256: evidence.diffSha256 };
  } else {
    requireString(qualification.reasonSha256, hashPattern, 'public ledger hotfix rationale digest');
    result.reasonSha256 = qualification.reasonSha256;
  }
  return result;
}

export function hotfixReasonDigest(reason) {
  requireThat(typeof reason === 'string' && !/[\u0000-\u001f\u007f]/.test(reason),
    'Hotfix requires a non-secret single-line rationale');
  const normalized = reason.trim().replace(/\s+/gu, ' ');
  requireThat(normalized.length >= 20 && normalized.length <= 2000, 'Hotfix rationale must explain non-promotion');
  return hash(normalized);
}

export function validatePromotionOrigin(state, qualification) {
  const origin = qualification.promotionOrigin;
  const candidate = state.reservations[origin.allocationKey];
  requireThat(candidate?.set && candidate.setHash === origin.setHash && candidate.record.channel === 'insider' &&
    candidate.record.sourceCommit === origin.sourceCommit && candidate.record.releaseId === origin.releaseId &&
    qualification.sourceCommit !== origin.sourceCommit,
  'Promotion requires a qualified immutable insider set and a distinct resulting main commit');
  return candidate;
}

export function validateReservationAdmission(state, admission) {
  validateAdmission(admission);
  const existing = state.reservations[allocationKey(admission)];
  if (existing) {
    requireThat(hash(existing.admission) === hash(admission), 'Same allocation key changed its admission');
    return existing;
  }
  const stableFloor = state.pointers.stable?.canonicalVersion ?? state.lastHistoricalStable;
  if (stableFloor !== undefined) {
    requireThat(compareVersions(admission.baseVersion, stableFloor) > 0,
      `New ${admission.channel} ${admission.channel === 'stable' ? 'canonical' : 'base'} version must exceed effective stable floor`);
  }
}

export function reserve(state, admission, created, protection, verifiedQualification) {
  validateLedger(state, state.anchor);
  const existing = validateReservationAdmission(state, admission);
  if (existing) return existing;
  const key = allocationKey(admission);
  const sequence = admission.channel === 'insider' ? (BigInt(state.counter) + 1n).toString() : undefined;
  const canonicalVersion = admission.baseVersion + (sequence ? `-${admission.stage}.${sequence}` : '');
  requireThat(!state.identities[canonicalVersion], 'Immutable identity already reserved');
  const previousStage = state.stages?.[admission.baseVersion];
  if (sequence && previousStage) {
    requireThat(compareVersions(canonicalVersion, previousStage) > 0,
      'Stage regression: beta < insider < rc; bump base before returning to an earlier stage');
  }
  const record = {
    schema: 1, ...admission, releaseId: `${admission.channel}:${canonicalVersion}`,
    canonicalVersion, sourceTag: `v${canonicalVersion}`, ...(sequence ? { sequence } : {}),
    allocationKey: key, created, protection,
  };
  if (admission.channel === 'stable') {
    const qualification = publicLedgerQualification(state.qualifications?.[admission.sourceCommit], admission.sourceCommit);
    requireThat(verifiedQualification && hash(verifiedQualification) === hash(qualification),
      'Stable qualification must be verified at authorization');
    record.qualification = qualification;
  }
  validateRecord(record);
  const reservation = { admission, record, ...(sequence ? { sequence } : {}) };
  state.reservations[key] = reservation;
  state.identities[canonicalVersion] = key;
  if (sequence) {
    state.counter = sequence;
    state.stages ??= {};
    state.stages[admission.baseVersion] = canonicalVersion;
  }
  return reservation;
}

export function verifyTag(record, expectedObject, actualTag) {
  requireThat(actualTag?.object === expectedObject && actualTag.commit === record.sourceCommit,
    'Source tag missing, moved, recreated, or not the exact authorized peeled SHA');
}

export function verifyConsumer(record, stored, context, identitySha256 = hash(stored)) {
  requireThat(hash(record) === identitySha256, 'Canonical record was changed');
  validateRecord(record);
  const tag = parseTag(record.sourceTag);
  requireThat(record.schema === 1 && record.repository === repository &&
    record.canonicalVersion === tag.canonicalVersion && record.baseVersion === tag.baseVersion &&
    record.channel === tag.channel && record.stage === tag.stage && record.sequence === tag.sequence &&
    record.releaseId === `${tag.channel}:${tag.canonicalVersion}` &&
    record.allocationKey === allocationKey(record) &&
    shaPattern.test(record.sourceCommit) && record.workflowCommit === record.sourceCommit &&
    record.workflowIdentity === `${repository}/${workflow}@refs/heads/${record.sourceBranch}`,
  'Invalid canonical record identity');
  requireThat(context.repository === repository &&
    context.workflowIdentity === record.workflowIdentity &&
    context.workflowSha === record.workflowCommit &&
    context.eventSha === record.sourceCommit &&
    context.ref === `refs/heads/${record.sourceBranch}` &&
    record.authorizedBranchHead === record.sourceCommit &&
    record.sourceBranch === (record.channel === 'stable' ? 'main' : 'development') &&
    ['stable', 'insider'].includes(record.channel) &&
    context.buildId === record.buildId && context.buildAttempt === record.buildAttempt &&
    ['workflow_dispatch', 'schedule'].includes(context.event),
  'Unauthorized consumer/caller/run/attempt');
}

export function identityLabels(record) {
  return {
    'org.opencontainers.image.version': record.canonicalVersion,
    'org.opencontainers.image.revision': record.sourceCommit,
    'org.opencontainers.image.source': `https://github.com/${record.repository}`,
    'org.opencontainers.image.created': record.created,
    'org.printfarmer.release-channel': record.channel,
    'org.printfarmer.release-id': record.releaseId,
    'org.printfarmer.build-id': record.buildId,
    'org.printfarmer.build-attempt': record.buildAttempt,
    'org.printfarmer.workflow': record.workflowIdentity,
    'org.printfarmer.identity-sha256': hash(record),
  };
}

export function validateCompleteSet(record, set) {
  validateRecord(record);
  requireObject(set, 'public set');
  requireObject(set.identity, 'public set identity');
  requireThat(set.schema === 1 && set.managedEligible === false,
    'Preparatory release sets are not managed eligibility manifests');
  requireThat(hash(set.identity) === hash(record), 'Set identity mismatch');
  writePublicSet(record, set);
}

export function advance(state, record, set, currentHead, expectedPointer) {
  validateLedger(state, state.anchor);
  validateRecord(record);
  validateCompleteSet(record, set);
  requireThat(record.sourceCommit === currentHead, 'Stale source cannot advance channel, regardless of N');
  const reservation = state.reservations[record.allocationKey];
  requireThat(reservation && (reservation.identitySha256 || hash(reservation.record)) === hash(record), 'Unknown authorization');
  const pointer = state.pointers[record.channel];
  requireThat((pointer?.setHash || '') === expectedPointer, 'Channel compare-and-set conflict');
  const setHash = hash(writePublicSet(record, set));
  if (reservation.setHash) requireThat(reservation.setHash === setHash, 'Same identity, different bytes');
  if (pointer?.releaseId === record.releaseId) {
    requireThat(pointer.setHash === setHash, 'Same identity, different bytes');
    return pointer;
  }
  if (pointer) requireThat(compareVersions(record.canonicalVersion, pointer.canonicalVersion) > 0,
    'Channel version regression');
  reservation.setHash = setHash;
  reservation.set = set;
  state.pointers[record.channel] = {
    releaseId: record.releaseId, canonicalVersion: record.canonicalVersion,
    sourceCommit: record.sourceCommit, setHash, allocationKey: record.allocationKey,
  };
  return state.pointers[record.channel];
}

// A Git ref fast-forward is the transaction. Two sibling commits cannot both win.
export async function transact(store, mutate, retries = 20) {
  for (let attempt = 0; attempt < retries; attempt++) {
    const { revision, state } = await store.read();
    validateLedger(state, state?.anchor);
    const result = await mutate(state);
    validateLedger(state, state.anchor);
    if (await store.compareAndSet(revision, state)) return result;
  }

  throw new Error('Ledger CAS contention; retry the same allocation key');
}

export function validateCandidate(candidate, now, maximumDays) {
  requireThat(Number.isInteger(maximumDays) && maximumDays > 0, 'Owner must choose candidate expiry limit');
  const target = parseTag(`v${candidate.target}`);
  requireThat(!target.stage && candidate.branch === `release/v${target.baseVersion}`, 'Invalid stabilization branch');
  requireThat(shaPattern.test(candidate.sourceCommit) && candidate.owner &&
    candidate.qualification && candidate.created && candidate.expires, 'Incomplete candidate ownership/evidence');
  const created = Date.parse(candidate.created);
  const expires = Date.parse(candidate.expires);
  requireThat(Number.isFinite(created) && Number.isFinite(expires) && expires > created &&
    expires - created <= maximumDays * 86400000 && Date.parse(now) < expires, 'Expired candidate or invalid lifetime');
  if (candidate.action === 'delete') {
    requireThat(candidate.mergeBack?.development === true && candidate.mergeBack?.activeCandidates === true &&
      candidate.mergeBack?.versionDidNotRegress === true, 'Candidate deletion requires merge-back parity');
    requireThat(candidate.publication || candidate.abandonmentReason, 'Retain candidate until publication or documented abandonment');
  }
  requireThat(!candidate.publish, 'Stabilization branches never publish');
  return candidate;
}
