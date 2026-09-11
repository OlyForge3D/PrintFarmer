import { writeFileSync, renameSync } from 'node:fs';

export function category(test, attempts) {
  if (attempts.some(result => result.status === 'interrupted')) return 'did-not-run';
  if (attempts.length === 0) return 'did-not-run';
  const last = attempts.at(-1);
  if (last.status === 'skipped') {
    return test.annotations.some(item => ['skip', 'fixme'].includes(item.type))
      ? 'skipped' : 'did-not-run';
  }
  return test.outcome() === 'unexpected' ? 'failed' : 'passed';
}

export default class DailyReporter {
  onBegin(config, suite) {
    this.startedAt = new Date().toISOString();
    this.tests = suite.allTests();
    this.attempts = new Map();
    this.errors = [];
    this.config = {
      workers: config.workers,
      projects: config.projects.map(project => ({
        name: project.name, retries: project.retries, timeout: project.timeout,
        use: { baseURL: project.use.baseURL, viewport: project.use.viewport },
      })),
    };
  }

  onTestEnd(test, result) {
    const attempts = this.attempts.get(test.id) ?? [];
    attempts.push({
      status: result.status, retry: result.retry, duration: result.duration,
      startedAt: result.startTime.toISOString(),
      errors: result.errors.map(error => ({ message: error.message, stack: error.stack })),
      attachments: result.attachments.map(item => ({
        name: item.name, path: item.path, contentType: item.contentType,
      })),
    });
    this.attempts.set(test.id, attempts);
  }

  onError(error) {
    this.errors ??= [];
    this.errors.push({ message: error.message, stack: error.stack });
  }

  onEnd(result) {
    const tests = (this.tests ?? []).map(test => {
      const attempts = this.attempts.get(test.id) ?? [];
      return {
        id: test.id, title: test.titlePath(), location: test.location,
        expectedStatus: test.expectedStatus, annotations: test.annotations,
        category: category(test, attempts), outcome: test.outcome(), attempts,
      };
    });
    const report = {
      schemaVersion: 1,
      validationId: process.env.PF_DAILY_ID,
      invocationId: process.env.PF_DAILY_INVOCATION,
      phase: process.env.PF_DAILY_PHASE,
      commit: process.env.PF_DAILY_COMMIT,
      manifestHash: process.env.PF_DAILY_MANIFEST_HASH,
      harnessHash: process.env.PF_DAILY_HARNESS_HASH,
      startedAt: this.startedAt, finishedAt: new Date().toISOString(),
      status: result.status, config: this.config, errors: this.errors, tests,
    };
    const target = process.env.PF_DAILY_RESULT;
    writeFileSync(`${target}.tmp`, JSON.stringify(report, undefined, 2), { mode: 0o600 });
    renameSync(`${target}.tmp`, target);
  }
}
