---
name: "ci-failure-triage"
description: "Triage failing PrintFarmer CI checks on a PR without blind reruns or scope creep. Use whenever required CI fails."
---

## Procedure

1. Identify the failing check, run, commit, and PR scope:

   ```bash
   gh pr checks <PR>
   gh run view <RUN_ID> --log-failed
   ```

   Confirm the run's head SHA is still the PR head before attributing results.
   Inspect the failed job's artifacts when `docs/CI.md` says it uploads them.
2. Classify the failure as **product**, **test**, **infrastructure**, or
   **flaky**. A flaky label requires evidence of nondeterminism or an unrelated
   baseline failure; one failing run alone is not proof.
3. If the PR caused a product or test failure, fix it in scope and obtain fresh
   CI and review evidence for the new head. If it is infrastructure or an
   unrelated flake, preserve the run/job evidence and do not broaden the PR to
   fix unrelated behavior.
4. Allow at most one rerun, only after reading the failed logs and when the
   evidence points to a transient runner/service problem. Do not use a rerun to
   hide a deterministic failure. If warranted, rerun failed jobs only:

   ```bash
   gh run rerun <RUN_ID> --failed
   ```

   A second failure ends rerun attempts.
5. Track a persistent flake as its own issue after checking for an existing
   duplicate. Keep the current PR focused; report the issue and the evidence.
   Do not expand another PR to fix the known unrelated iPhone
   `LoginFlowUITests` password-entry flake; report it separately. If it is a
   required red check, it still blocks landing under the same-head CI rule in
   [PR landing](../pr-landing/SKILL.md).
6. The XCUI runner has a separate, tightly scoped retry of failed test IDs.
   A test that passes that retry makes the job pass with a **passed on retry
   (flaky)** warning; other test, setup, timeout, or incomplete-evidence
   failures remain failures. Do not confuse this built-in retry with rerunning
   an entire Actions run. See the
   [XCUI retry policy](../../../mobile/docs/xcui-timeout-diagnosis.md#one-failed-test-retry-in-normal-xcui-ci).
7. A failed required check blocks PR landing. Flaky or unrelated failures do
   not waive required checks. Do not rerun a `squad-review-verdict` workflow
   run; its verifier rejects rerun attempts. For verdict recovery, use the
   current procedure in
   [Ralph's merge reference](../../ralph-reference.md).

## References

- [CI workflow map and failure diagnosis](../../../docs/CI.md#failure-diagnosis)
- [PR landing procedure](../pr-landing/SKILL.md)
