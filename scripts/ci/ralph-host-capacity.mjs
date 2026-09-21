import { readFileSync } from 'node:fs';

const configuration = JSON.parse(readFileSync(new URL('../../.copilot/skills/ralph-loop/hosts.json', import.meta.url), 'utf8'));
const mobileSignal = /\b(ios|mobile|swiftui|swift|xcui|xctest|xcode|testflight|apns)\b/i;
const mobilePath = /(^mobile\/|\.swift$|\.xcodeproj(?:\/|$)|\.xcworkspace(?:\/|$))/i;

export function classifyWork(work) {
  const files = Array.isArray(work?.files) ? work.files : [];
  const labels = Array.isArray(work?.labels) ? work.labels : [];
  const text = [work?.title, ...(Array.isArray(work?.acceptanceCriteria) ? work.acceptanceCriteria : [])].filter(Boolean).join('\n');
  if (work?.scope !== 'general' || work.classificationComplete !== true ||
      files.some((file) => typeof file !== 'string' || mobilePath.test(file)) ||
      labels.some((label) => typeof label !== 'string' || mobileSignal.test(label)) ||
      mobileSignal.test(text)) return 'mobile';
  return 'general';
}

export function hostLimits(host) {
  const profile = configuration.hosts?.[host];
  if (!profile?.configured || !['macos-mobile', 'windows-general'].includes(host) ||
      profile.maxLocalSessions !== 5 || !Number.isInteger(profile.maxMobileSessions) ||
      !Number.isInteger(profile.maxGeneralSessions) || profile.maxMobileSessions + profile.maxGeneralSessions !== 5 ||
      (host === 'macos-mobile' && (profile.maxMobileSessions !== 1 || profile.maxGeneralSessions !== 4)) ||
      (host === 'windows-general' && (profile.maxMobileSessions !== 0 || profile.maxGeneralSessions !== 5))) {
    throw new Error('Unknown or invalid host capacity policy.');
  }
  return { total: 5, mobile: profile.maxMobileSessions, general: profile.maxGeneralSessions };
}

export function assessHostCapacity({ host, inventory, candidate, now = Date.now() }) {
  const limits = hostLimits(host);
  if (!candidate || typeof candidate !== 'object' || Array.isArray(candidate)) throw new Error('A classified candidate is required for capacity evaluation.');
  const observed = Date.parse(inventory?.observedAt);
  if (inventory?.complete !== true || inventory.historyChecked !== true || inventory.queueChecked !== true ||
      inventory.reservationsChecked !== true || inventory.remoteOwnershipChecked !== true ||
      typeof inventory.source !== 'string' || !inventory.source.trim() || !Array.isArray(inventory.work) || !Number.isFinite(observed) ||
      observed > now || now - observed > 60_000) {
    throw new Error('Fresh complete native/queued/history/reservation/remote ownership inventory is required for category capacity.');
  }
  const groups = [];
  for (const work of inventory.work) {
    if (!work || !['macos-mobile', 'windows-general'].includes(work.executionHost) ||
        !['reserved', 'queued', 'active', 'uncertain', 'terminal'].includes(work.state) ||
        ![work.jobId, work.sessionId].some((id) => typeof id === 'string' && id.length > 0) ||
        [work.jobId, work.sessionId].some((id) => id !== undefined && (typeof id !== 'string' || !/^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/.test(id)))) {
      throw new Error('Capacity work requires a real job/session identity, execution host and retained state.');
    }
    if (work.state === 'terminal' && work.terminalVerified === true) continue;
    const aliases = [work.jobId && `job:${work.jobId}`, work.sessionId && `session:${work.sessionId}`].filter(Boolean);
    const related = groups.filter((group) => aliases.some((alias) => group.aliases.has(alias)));
    const merged = {
      aliases: new Set(aliases), hosts: new Set([work.executionHost]),
      category: classifyWork(work),
    };
    for (const group of related) {
      for (const alias of group.aliases) merged.aliases.add(alias);
      for (const owner of group.hosts) merged.hosts.add(owner);
      if (group.category === 'mobile') merged.category = 'mobile';
      groups.splice(groups.indexOf(group), 1);
    }
    if (merged.hosts.size !== 1) throw new Error('Conflicting execution-host observations cannot free capacity.');
    groups.push(merged);
  }
  const counts = { total: 0, mobile: 0, general: 0 };
  for (const group of groups) {
    if (!group.hosts.has(host)) continue;
    counts.total++;
    counts[group.category]++;
  }
  const category = classifyWork(candidate);
  const existing = candidate && groups.find((group) =>
    (candidate.sessionId && group.aliases.has(`session:${candidate.sessionId}`)) ||
    (candidate.jobId && group.aliases.has(`job:${candidate.jobId}`)));
  if (existing && (!existing.hosts.has(host) || existing.category !== category)) {
    throw new Error('A recovery handoff cannot change host/category or reuse another owner identity to bypass capacity.');
  }
  const projected = { ...counts };
  if (!existing) { projected.total++; projected[category]++; }
  const allowed = projected.total <= limits.total && projected.mobile <= limits.mobile && projected.general <= limits.general;
  return {
    allowed, category, counts, projected, limits, dispatchAuthorized: false,
    reason: allowed ? 'Category capacity only; live ownership and atomic admission remain mandatory.' : 'Hard host/category capacity reached; idle category slots cannot be borrowed.',
  };
}
