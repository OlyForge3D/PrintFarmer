import { beforeEach, describe, expect, it, vi } from 'vitest';

const { get, post } = vi.hoisted(() => ({ get: vi.fn(), post: vi.fn() }));
vi.mock('@/services/api/httpClient', () => ({ client: { get, post } }));

import {
  clearDispatchRecoveryBlock,
  getDispatchReconciliation,
  getDispatchRecoveryAudit,
  getQueueCandidatePage,
  recoverDispatchClaim,
} from '@/services/api/dispatchRecoveryApi';

const body = {
  dispatchAttemptId: 'attempt-1',
  claimRevision: 3,
  physicalCheckConfirmed: true,
  senderIsolationConfirmed: false,
  note: null,
};

describe('dispatchRecoveryApi', () => {
  beforeEach(() => {
    get.mockReset();
    post.mockReset();
  });

  it('reads the reconciliation resource and its ETag header', async () => {
    get.mockResolvedValue({ data: { printerId: 'p 1' }, headers: { etag: '"abc="' } });

    const snapshot = await getDispatchReconciliation('p 1');

    expect(get).toHaveBeenCalledWith('/dispatch/p%201/reconciliation');
    expect(snapshot).toEqual({ resource: { printerId: 'p 1' }, etag: '"abc="' });
  });

  it('reports a null ETag when the server sends none', async () => {
    get.mockResolvedValue({ data: { printerId: 'p1' }, headers: {} });
    await expect(getDispatchReconciliation('p1')).resolves.toMatchObject({ etag: null });
  });

  it('reads an unfiltered candidate page and the X-Has-More continuation header', async () => {
    get.mockResolvedValue({ data: [], headers: { 'x-has-more': 'true' } });

    const page = await getQueueCandidatePage(1000, 2000);

    expect(get).toHaveBeenCalledWith('/job-queue-analytics?sortBy=priority&limit=1000&offset=2000');
    expect(page).toEqual({ jobs: [], hasMore: true });
  });

  it.each([
    [{ 'x-has-more': 'false' }, false],
    [{}, null],
    [{ 'x-has-more': 'maybe' }, null],
  ])('maps X-Has-More headers %j to %s', async (headers, expected) => {
    get.mockResolvedValue({ data: [], headers });
    await expect(getQueueCandidatePage(10, 0)).resolves.toMatchObject({ hasMore: expected });
  });

  it('sends If-Match and Idempotency-Key on recover and maps 200', async () => {
    post.mockResolvedValue({
      status: 200,
      data: { printerId: 'p1', hasIndeterminateClaim: false, recoveryAuditId: 'audit-1' },
      headers: { etag: '"next="' },
    });

    const result = await recoverDispatchClaim({
      printerId: 'p1',
      etag: '"rev="',
      idempotencyKey: ' key-1 ',
      body,
    });

    expect(post).toHaveBeenCalledTimes(1);
    const [url, sentBody, config] = post.mock.calls[0];
    expect(url).toBe('/dispatch/p1/reconciliation/recover');
    expect(sentBody).toEqual(body);
    expect(config.headers).toEqual({ 'If-Match': '"rev="', 'Idempotency-Key': 'key-1' });
    expect(config.validateStatus(412)).toBe(true);
    expect(config.validateStatus(500)).toBe(false);
    expect(result).toMatchObject({ kind: 'recovered', etag: '"next="' });
  });

  it('quotes a bare ETag', async () => {
    post.mockResolvedValue({ status: 200, data: {}, headers: {} });
    await recoverDispatchClaim({ printerId: 'p1', etag: 'rev=', idempotencyKey: 'k', body });
    expect(post.mock.calls[0][2].headers['If-Match']).toBe('"rev="');
  });

  it('refuses to send recovery without an ETag or idempotency key', async () => {
    await expect(
      recoverDispatchClaim({ printerId: 'p1', etag: '', idempotencyKey: 'k', body })
    ).rejects.toThrow(/ETag/);
    await expect(
      recoverDispatchClaim({ printerId: 'p1', etag: '"x"', idempotencyKey: '  ', body })
    ).rejects.toThrow(/Idempotency-Key/);
    expect(post).not.toHaveBeenCalled();
  });

  it.each([
    [412, 'rejected_stale', 'stale'],
    [409, 'rejected_sender_live', 'conflict'],
    [403, 'forbidden', 'forbidden'],
    [404, 'printer_not_found', 'not_found'],
    [400, 'physical_check_required', 'invalid'],
    [428, 'precondition_required', 'invalid'],
  ])('maps HTTP %i to a %s failure', async (status, error, kind) => {
    post.mockResolvedValue({
      status,
      data: { error, detail: 'd', liveSender: status === 409 ? 'start' : undefined },
      headers: {},
    });

    const result = await recoverDispatchClaim({
      printerId: 'p1',
      etag: '"x"',
      idempotencyKey: 'k',
      body,
    });

    expect(result).toMatchObject({ kind, httpStatus: status, errorCode: error, detail: 'd' });
    if (status === 409) {
      expect(result).toMatchObject({ liveSender: 'start' });
    }
  });

  it('fetches a recovery audit record', async () => {
    get.mockResolvedValue({ data: { auditId: 'a1' }, headers: {} });
    await expect(getDispatchRecoveryAudit('p1', 'a1')).resolves.toEqual({ auditId: 'a1' });
    expect(get).toHaveBeenCalledWith('/dispatch/p1/reconciliation/audit/a1');
  });

  it('clears a recovery block with the job rowVersion as If-Match', async () => {
    post.mockResolvedValue({ status: 200, data: { jobId: 'j1' }, headers: {} });

    const result = await clearDispatchRecoveryBlock({ jobId: 'j1', jobETag: 'AAAAAAAAB9E=' });

    const [url, sentBody, config] = post.mock.calls[0];
    expect(url).toBe('/dispatch/jobs/j1/recovery/clear');
    expect(sentBody).toBeUndefined();
    expect(config.headers).toEqual({ 'If-Match': '"AAAAAAAAB9E="' });
    expect(result).toMatchObject({ kind: 'cleared', jobId: 'j1' });
  });

  it('maps clear conflicts and refuses a missing job ETag', async () => {
    post.mockResolvedValue({ status: 412, data: { error: 'job_revision_conflict' }, headers: {} });
    await expect(
      clearDispatchRecoveryBlock({ jobId: 'j1', jobETag: '"x"' })
    ).resolves.toMatchObject({ kind: 'stale', errorCode: 'job_revision_conflict' });

    post.mockClear();
    await expect(clearDispatchRecoveryBlock({ jobId: 'j1', jobETag: null })).rejects.toThrow(
      /ETag/
    );
    expect(post).not.toHaveBeenCalled();
  });
});
