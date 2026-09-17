import { repository, requireThat } from './release-policy.mjs';

const root = `https://api.github.com/repos/${repository}/`;
// GitHub canonicalizes Link paths to this repository's immutable numeric ID.
const numericRoot = 'https://api.github.com/repositories/1044049720/';
const maximumPages = 100;

export function evidenceCollection(endpoint) {
  if (typeof endpoint !== 'string') return undefined;
  if (/^commits\/[a-f0-9]{40}\/check-runs\?per_page=100$/.test(endpoint)) return 'check_runs';
  if (/^commits\/[a-f0-9]{40}\/status\?per_page=100$/.test(endpoint)) return 'statuses';
  if (/^actions\/runs\/[1-9][0-9]*\/attempts\/[1-9][0-9]*\/jobs\?per_page=100$/.test(endpoint)) return 'jobs';
  if (/^actions\/workflows\/consolidated-release\.yml\/runs\?status=(?:queued|in_progress|waiting|pending|requested)&per_page=100$/.test(endpoint)) return 'workflow_runs';
  if (/^actions\/workflows\/(?:ci|qualify-canonical-release)\.yml\/runs\?(?:head_sha=[a-f0-9]{40}|created=%3E%3D(?:[0-9TZ.-]|%3A)+)&per_page=100$/.test(endpoint)) return 'workflow_runs';
  if (/^(?:commits\/[a-f0-9]{40}\/(?:comments|statuses|pulls)|pulls\/[1-9][0-9]*\/reviews)\?per_page=100$/.test(endpoint)) return '';
  return undefined;
}

export function evidenceBaseEndpoint(endpoint) {
  requireThat(typeof endpoint === 'string', 'Invalid evidence endpoint');
  const base = endpoint.replace(/&page=[1-9][0-9]*$/, '');
  return evidenceCollection(base) === undefined ? endpoint : base;
}

function pageLinks(link, endpoint, page) {
  const relations = new Map();
  if (!link) return relations;
  const expected = new URL(`${root}${endpoint}`);
  const numericPath = new URL(`${numericRoot}${endpoint}`).pathname;
  for (const part of link.split(',')) {
    const match = /^\s*<([^>]+)>;\s*rel="(next|prev|first|last)"\s*$/.exec(part);
    requireThat(match && !relations.has(match[2]), 'Malformed or duplicate evidence pagination link');
    requireThat(URL.canParse(match[1]), 'Malformed evidence pagination URL');
    const url = new URL(match[1]);
    const number = url.searchParams.get('page');
    const params = new URLSearchParams(url.search);
    params.delete('page');
    params.sort();
    const expectedParams = new URLSearchParams(expected.search);
    expectedParams.sort();
    requireThat(url.origin === expected.origin && url.username === '' && url.password === '' &&
      url.hash === '' && (url.pathname === expected.pathname || url.pathname === numericPath) &&
      url.searchParams.getAll('page').length === 1 && /^[1-9][0-9]*$/.test(number ?? '') &&
      Number.isSafeInteger(Number(number)) && Number(number) <= maximumPages &&
      params.toString() === expectedParams.toString(), 'Untrusted evidence pagination link');
    const target = Number(number);
    requireThat((match[2] !== 'next' || target === page + 1) &&
      (match[2] !== 'prev' || target === page - 1) &&
      (match[2] !== 'first' || target === 1), 'Skipped or repeated evidence page');
    relations.set(match[2], target);
  }
  return relations;
}

// Keep the API's response shape, but return it only after every page is proven complete.
export async function readEvidencePages(endpoint, request) {
  const field = evidenceCollection(endpoint);
  requireThat(field !== undefined, 'Unsupported evidence collection');
  const entries = [];
  const seen = new Set();
  let first;
  let total;
  let last;
  for (let page = 1; page <= maximumPages; page++) {
    const { data, link } = await request(page === 1 ? endpoint : `${endpoint}&page=${page}`);
    const items = field ? data?.[field] : data;
    requireThat(Array.isArray(items) && items.length <= 100, 'Malformed evidence page');
    if (page === 1) {
      first = data;
      total = field ? data.total_count : undefined;
    }
    if (field) {
      requireThat(Number.isSafeInteger(total) && total >= 0 && total <= maximumPages * 100 &&
        data.total_count === total && items.length === Math.min(100, total - entries.length),
      'Missing, changed or truncated evidence total');
      if (field === 'statuses') {
        requireThat(data.sha === endpoint.split('/')[1], 'Status evidence page is not bound to exact SHA');
      }
    }
    for (const item of items) {
      requireThat(item && Number.isSafeInteger(item.id) && item.id > 0 && !seen.has(item.id),
        'Duplicate or unidentified evidence entry');
      seen.add(item.id);
      entries.push(item);
    }
    const links = pageLinks(link, endpoint, page);
    const next = links.has('next');
    // Reaching another uncounted page proves the preceding page was full.
    const terminalProbe = !field && page > 1 && items.length === 0 && !next;
    if (links.has('last')) {
      requireThat(links.get('last') >= page ||
        (terminalProbe && links.get('last') === page - 1), 'Skipped or repeated evidence page');
      requireThat(last === undefined || last === links.get('last'), 'Changed evidence last page');
      last = links.get('last');
      if (field) requireThat(last === Math.max(1, Math.ceil(total / 100)), 'Inconsistent evidence last page');
    }
    const complete = field ? entries.length === total : items.length < 100;
    requireThat(!next || (!complete && items.length === 100), 'Truncated or surplus evidence page');
    if (complete) {
      requireThat(last === undefined || last === page ||
        (terminalProbe && last === page - 1), 'Missing terminal evidence page');
      return field ? { ...first, [field]: entries } : entries;
    }
    requireThat(!field || next, 'Missing evidence next page');
    // Arrays have no total_count. A full terminal page needs an explicit empty-page probe.
    requireThat(last === undefined || page <= last, 'Evidence exceeds advertised last page');
  }
  throw new Error(`Evidence page budget exceeded (${maximumPages} pages, including any empty terminal probe)`);
}
