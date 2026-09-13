import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { requireThat, validateCandidate } from './release-policy.mjs';

const branch = process.env.CANDIDATE_BRANCH || '';
try {
  requireThat(branch !== 'release', 'Bare release branch is not a stabilization candidate');
  if (branch.startsWith('release/')) {
    const candidate = JSON.parse(readFileSync('.github/release-candidate.json', 'utf8'));
    requireThat(candidate.branch === branch, 'Candidate plan does not describe this branch');
    validateCandidate(candidate, new Date().toISOString(), Number(process.env.RELEASE_CANDIDATE_MAX_DAYS));
    execFileSync('git', ['merge-base', '--is-ancestor', candidate.sourceCommit, 'HEAD'], { stdio: 'pipe' });
    console.log(`Validated ${branch}; publication is prohibited`);
  }
} catch (error) {
  console.error(error.message);
  process.exitCode = 1;
}
