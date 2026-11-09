export function validateCatalog(catalog) {
  const errors = [];
  if (catalog.schemaVersion !== 1) errors.push('schemaVersion must be 1');
  if (!/^[0-9A-F]{64}$/.test(catalog.source?.sha256 ?? '')) errors.push('source.sha256 must be a SHA-256 digest');
  if (!Array.isArray(catalog.entries) || catalog.entries.length === 0) {
    errors.push('entries must be a non-empty array');
    return errors;
  }

  const ids = new Set();
  for (const [index, entry] of catalog.entries.entries()) {
    const label = `entries[${index}]`;
    if (!/^[a-z0-9]+(?:[.-][a-z0-9]+)*$/.test(entry.id ?? '')) errors.push(`${label}.id must be a stable lowercase identifier`);
    if (ids.has(entry.id)) errors.push(`${label}.id duplicates ${entry.id}`);
    ids.add(entry.id);
    if (!['web', 'ios', 'site'].includes(entry.platform)) errors.push(`${label}.platform is invalid`);
    if (typeof entry.inventoryRow !== 'string' || entry.inventoryRow.length === 0) errors.push(`${label}.inventoryRow is required`);
    if (!Array.isArray(entry.evidenceFlags) || entry.evidenceFlags.length === 0 || entry.evidenceFlags.some(flag => !['V', 'I', 'U'].includes(flag))) {
      errors.push(`${label}.evidenceFlags must preserve at least one V/I/U flag`);
    }
    if (!['candidate', 'blocked', 'unverified'].includes(entry.disposition)) errors.push(`${label}.disposition is invalid`);
    if (typeof entry.reason !== 'string' || entry.reason.length === 0) errors.push(`${label}.reason must explain its disposition`);
    if (entry.disposition === 'candidate' && (!Array.isArray(entry.scenarioIds) || entry.scenarioIds.length === 0)) {
      errors.push(`${label}.candidate must name an executable scenario`);
    }
    if (entry.disposition !== 'candidate' && entry.scenarioIds !== undefined) {
      errors.push(`${label}.scenarioIds are reserved for executable candidates`);
    }
  }

  return errors;
}

export function validateApprovalDocument(approvals, catalog, scenarioIds) {
  const errors = [];
  if (approvals.schemaVersion !== 1) errors.push('schemaVersion must be 1');
  if (approvals.sourceInventorySha256 !== catalog.source.sha256) errors.push('sourceInventorySha256 does not match the inventory');
  if (approvals.renderProfileId !== catalog.renderProfile.id) errors.push('renderProfileId does not match the pinned renderer');
  if (!Array.isArray(approvals.scenarios)) {
    errors.push('scenarios must be an array');
    return errors;
  }

  const expected = new Set(scenarioIds);
  const actual = new Set();
  for (const scenario of approvals.scenarios) {
    if (actual.has(scenario.id)) errors.push(`duplicate approval record for ${scenario.id}`);
    actual.add(scenario.id);
    if (!expected.has(scenario.id)) errors.push(`approval record is not a catalog candidate: ${scenario.id}`);
    if (typeof scenario.baseline !== 'string' || scenario.baseline.length === 0) errors.push(`${scenario.id} has no baseline path`);
    if (!['pending-human-review', 'approved'].includes(scenario.status)) errors.push(`${scenario.id} has an invalid approval status`);
    if (scenario.status === 'approved' && !/^[0-9a-f]{64}$/i.test(scenario.baselineSha256 ?? '')) {
      errors.push(`${scenario.id} has no valid approved baseline SHA-256 digest`);
    }
  }
  for (const id of expected) if (!actual.has(id)) errors.push(`missing approval record for ${id}`);
  return errors;
}

export function assertApprovedGolden({ approval, sourceSha256, renderProfileId, baselineExists, baselineSha256 }) {
  if (!approval || approval.status !== 'approved') {
    throw new Error('Golden is pending human review; candidate screenshots are not comparison baselines.');
  }
  if (!approval.approvedBy || !approval.approvedAt || !/^[0-9a-f]{40}$/i.test(approval.approvedSourceSha ?? '')) {
    throw new Error('Golden approval must name a reviewer, timestamp, and exact 40-character source SHA.');
  }
  if (!/^[0-9a-f]{64}$/i.test(approval.baselineSha256 ?? '')) {
    throw new Error('Golden approval must pin the exact baseline PNG SHA-256 digest.');
  }
  if (sourceSha256 !== approval.sourceInventorySha256 || renderProfileId !== approval.renderProfileId) {
    throw new Error('Golden approval is stale for the current inventory or rendering profile.');
  }
  if (!baselineExists) throw new Error(`Approved golden is missing: ${approval.baseline}`);
  if (baselineSha256?.toLowerCase() !== approval.baselineSha256.toLowerCase()) {
    throw new Error(`Approved golden has changed since approval: ${approval.baseline}`);
  }
}
