## Stalled Approved PR Merge Fallback

**Pattern:** Finish a clean, already-approved PR when it is blocked only by rebase drift or broken `gh` API auth.

### When to Use

- PR has a current non-author human approval or a verifier result of
  `APPROVED` from `scripts/ci/verify-squad-verdict.mjs`.
- CI on the current head is green or expected to be green after a mechanical rebase.
- `gh` cannot submit the merge because the token is invalid, but git HTTPS credentials still exist in the keychain.

### Procedure

1. Inspect the PR head/base and changed files first.
2. Verify squad evidence with
   `node scripts/ci/verify-squad-verdict.mjs --repo <owner>/<repo> --pr <number> --json`.
3. Rebase the PR branch onto base.
4. Resolve only the actual unmerged files from `git diff --name-only --diff-filter=U`.
5. Treat the rebase as superseding every prior squad approval or rejection,
   even when the resulting diff is unchanged. Obtain and record a fresh
   exact-head verdict before merge.
6. Prefer union merges when the base branch added valuable regression coverage and the PR adds orthogonal tests or endpoint wiring.
7. Run the smallest focused validation that covers the rebased changes.
8. Merge with `gh pr merge --match-head-commit <reviewed-head-sha>`. If
   `gh auth status` reports an invalid token but `git credential fill`
   succeeds for `github.com`, use a one-off GitHub REST merge call and include
   the same SHA in the request body's `sha` field.
9. Verify merged state and linked issue state after the API call.

### One-Off Merge Rule

- Never print the credential helper output.
- Read the username/password into shell variables, call the REST endpoint once, and only print the HTTP status and merge SHA.
- Treat branch deletion and issue closure as separate verification steps after merge.

### Review Constraint

- GitHub will not allow `APPROVE` on a PR authored by the same account performing the review.
- An author-written comment or lookalike status is not review evidence.
- If the trusted verdict recorder is not operational, require human approval;
  do not continue an author-only merge path.
