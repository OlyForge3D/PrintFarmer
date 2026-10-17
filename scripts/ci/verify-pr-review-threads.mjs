#!/usr/bin/env node

import { pathToFileURL } from 'node:url';
import { readGhJson } from './gh-api.mjs';

const headFields = 'headRefOid';
const threadFields = `
  reviewThreads(first: 100, after: $after) {
    totalCount
    pageInfo { hasNextPage endCursor }
    nodes {
      id isResolved isOutdated path line
      comments(first: 1) { nodes { url author { login } } }
    }
  }`;
const shaPattern = /^[0-9a-f]{40}$/;

function fail(classification, reason) {
  throw Object.assign(new Error(reason), { classification });
}

export function exitCodeFor(classification) {
  switch (classification) {
    case 'CLEAR': return 0;
    case 'UNRESOLVED_REVIEW_THREADS': return 2;
    case 'HEAD_MISMATCH': return 3;
    default: return 1;
  }
}

function parseArgs(argv) {
  const args = {};
  for (let index = 0; index < argv.length; index += 1) {
    const argument = argv[index];
    if (argument === '--json') {
      args.json = true;
    } else if (['--repo', '--pr', '--expected-head'].includes(argument)) {
      const value = argv[++index];
      if (!value || value.startsWith('--') || args[argument.slice(2)] !== undefined) {
        throw new Error(`Missing or duplicate value for ${argument}.`);
      }
      args[argument.slice(2)] = value;
    } else {
      throw new Error(`Unknown argument: ${argument}`);
    }
  }
  if (!/^[A-Za-z0-9._-]+\/[A-Za-z0-9._-]+$/.test(args.repo ?? '')) {
    throw new Error('--repo must be OWNER/REPOSITORY.');
  }
  if (!/^[1-9]\d*$/.test(args.pr ?? '') || !Number.isSafeInteger(Number(args.pr))) {
    throw new Error('--pr must be a positive safe integer.');
  }
  if (args['expected-head'] !== undefined && !shaPattern.test(args['expected-head'])) {
    throw new Error('--expected-head must be a lowercase 40-character SHA.');
  }
  return { repo: args.repo, pr: Number(args.pr), expectedHead: args['expected-head'] };
}

function ghGraphql(query, variables) {
  return readGhJson([
    'api', 'graphql', '-f', `query=${query}`,
    ...Object.entries(variables).flatMap(([key, value]) =>
      [typeof value === 'number' ? '-F' : '-f', `${key}=${value}`]),
  ]);
}

export async function verifyPrReviewThreads({ repo, pr, expectedHead, api = ghGraphql }) {
  const [owner, name] = repo.split('/');
  let observedHead;
  async function readPull(after, includeThreads) {
    const query = `query($owner: String!, $name: String!, $number: Int!${
      includeThreads ? ', $after: String' : ''
    }) {
      repository(owner: $owner, name: $name) {
        pullRequest(number: $number) { ${headFields} ${includeThreads ? threadFields : ''} }
      }
    }`;
    let response;
    try {
      response = await api(query, {
        owner, name, number: pr, ...(after === undefined ? {} : { after }),
      });
    } catch (error) {
      fail('API_ERROR', `GitHub API request failed: ${error.message}`);
    }
    if (response?.errors !== undefined &&
        (!Array.isArray(response.errors) || response.errors.length > 0)) {
      fail('GRAPHQL_ERROR', `GitHub GraphQL errors: ${JSON.stringify(response.errors)}`);
    }
    const pull = response?.data?.repository?.pullRequest;
    if (!pull || !shaPattern.test(pull.headRefOid ?? '')) {
      fail('MALFORMED_RESPONSE', 'Missing PR or valid live head SHA.');
    }
    const requiredHead = expectedHead ?? observedHead;
    if (requiredHead !== undefined && pull.headRefOid !== requiredHead) {
      fail('HEAD_MISMATCH', `Expected head ${requiredHead}; live head is ${pull.headRefOid}.`);
    }
    observedHead = pull.headRefOid;
    return pull;
  }

  const threads = [];
  const ids = new Set();
  const cursors = new Set();
  let after;
  let totalCount;
  do {
    const { reviewThreads: page } = await readPull(after, true);
    if (!page || !Number.isSafeInteger(page.totalCount) || page.totalCount < 0 ||
        !Array.isArray(page.nodes) || page.nodes.length > 100 ||
        typeof page.pageInfo?.hasNextPage !== 'boolean') {
      fail('MALFORMED_PAGE', 'Missing or malformed review-thread connection.');
    }
    totalCount ??= page.totalCount;
    if (page.totalCount !== totalCount) {
      fail('INCOMPLETE_PAGE', 'Review-thread count changed during pagination; retry verification.');
    }
    for (const thread of page.nodes) {
      const comment = thread?.comments?.nodes?.[0];
      if (typeof thread?.id !== 'string' || !thread.id ||
          typeof thread.isResolved !== 'boolean' || typeof thread.isOutdated !== 'boolean' ||
          typeof thread.path !== 'string' || !thread.path ||
          !(thread.line === null || (Number.isSafeInteger(thread.line) && thread.line > 0)) ||
          thread.comments?.nodes?.length !== 1 ||
          typeof comment?.url !== 'string' || !/^https:\/\/[^/\s]+\/\S+$/.test(comment.url) ||
          !(comment.author === null ||
            (typeof comment.author?.login === 'string' && comment.author.login.length > 0))) {
        fail('MALFORMED_PAGE', 'Missing or malformed review-thread diagnostics.');
      }
      if (ids.has(thread.id)) {
        fail('INCOMPLETE_PAGE', `Duplicate review thread ${thread.id}.`);
      }
      ids.add(thread.id);
      threads.push({
        id: thread.id, isResolved: thread.isResolved, isOutdated: thread.isOutdated,
        path: thread.path, line: thread.line, url: comment.url,
        author: comment.author?.login ?? '[deleted]',
      });
    }
    const { hasNextPage, endCursor } = page.pageInfo;
    if (threads.length > totalCount ||
        (hasNextPage && (page.nodes.length === 0 || threads.length >= totalCount ||
          typeof endCursor !== 'string' || !endCursor || cursors.has(endCursor)))) {
      fail('INCOMPLETE_PAGE', 'Invalid or non-advancing review-thread pagination.');
    }
    if (!hasNextPage) {
      if (threads.length !== totalCount) {
        fail('INCOMPLETE_PAGE', `Expected ${totalCount} review threads; received ${threads.length}.`);
      }
      break;
    }
    cursors.add(endCursor);
    after = endCursor;
  } while (true);

  // Recheck after the last page too; a force-push can happen during pagination.
  await readPull(undefined, false);
  const unresolvedCount = threads.filter((thread) => !thread.isResolved).length;
  return {
    classification: unresolvedCount ? 'UNRESOLVED_REVIEW_THREADS' : 'CLEAR',
    reason: unresolvedCount ? `UNRESOLVED_REVIEW_THREADS: ${unresolvedCount}` :
      `No unresolved review threads at ${observedHead}.`,
    repo, pr, headSha: observedHead, unresolvedCount, threads,
  };
}

export async function runCli(argv, { api = ghGraphql, write = (text) => process.stdout.write(text) } = {}) {
  let report;
  try {
    const args = parseArgs(argv);
    report = await verifyPrReviewThreads({ ...args, api });
  } catch (error) {
    report = {
      classification: error.classification ?? 'VERIFIER_ERROR',
      reason: error.message,
    };
  }
  if (argv.includes('--json')) {
    write(`${JSON.stringify(report)}\n`);
  } else {
    write(`${report.classification}: ${report.reason}\n`);
    for (const thread of report.threads ?? []) {
      write(`${thread.isResolved ? 'RESOLVED' : 'UNRESOLVED'} ${thread.url} ` +
        `author=${thread.author} path=${thread.path}:${thread.line ?? '?'} ` +
        `outdated=${thread.isOutdated}\n`);
    }
  }
  return exitCodeFor(report.classification);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  process.exitCode = await runCli(process.argv.slice(2));
}
