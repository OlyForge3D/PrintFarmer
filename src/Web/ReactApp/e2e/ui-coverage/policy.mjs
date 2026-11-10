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
