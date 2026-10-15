import { describe, expect, it } from 'vitest';
import { decodePrinterFilamentCoverage } from '../types';
import { hasFilamentCoverageDemand, withOfflineOverride } from '../utils';

const idleCoverage = decodePrinterFilamentCoverage({
  status: 'covers', activeJobId: null, assignedQueuedJobCount: 0,
  toolheads: [{ status: 'runout', remainingGrams: 100, totalDemandGrams: 200 }],
});

describe('hasFilamentCoverageDemand', () => {
  it.each([null, undefined, idleCoverage])('hides absent or idle coverage (%s)', (coverage) => {
    expect(hasFilamentCoverageDemand(coverage)).toBe(false);
  });

  it('recognizes an active job even when its material demand is unknown', () => {
    expect(hasFilamentCoverageDemand({ ...idleCoverage, activeJobId: 'job-1' })).toBe(true);
  });

  it('recognizes assigned queued work without an active job', () => {
    expect(hasFilamentCoverageDemand({ ...idleCoverage, assignedQueuedJobCount: 1 })).toBe(true);
  });

  it.each([-1, 0, NaN, Infinity])('rejects invalid or empty queue counts (%s)', (count) => {
    expect(hasFilamentCoverageDemand({ ...idleCoverage, activeJobId: ' ', assignedQueuedJobCount: count })).toBe(false);
  });

  it('does not infer demand from spool weights, status or per-toolhead estimates', () => {
    expect(hasFilamentCoverageDemand(idleCoverage)).toBe(false);
  });
});

describe('withOfflineOverride demand visibility', () => {
  it.each([true, false])('hides idle coverage whether online is %s', (isOnline) => {
    expect(withOfflineOverride(idleCoverage, isOnline)).toBeNull();
  });

  it('preserves online coverage with demand', () => {
    const active = { ...idleCoverage, activeJobId: 'job-1' };
    expect(withOfflineOverride(active, true)).toBe(active);
  });

  it('keeps offline demand visible but unverified without mutating source data', () => {
    const queued = { ...idleCoverage, assignedQueuedJobCount: 1 };
    const result = withOfflineOverride(queued, false);
    expect(result?.status).toBe('unknown');
    expect(result?.toolheads[0]).toEqual({ ...queued.toolheads[0], status: 'unknown' });
    expect(queued.status).toBe('covers');
    expect(queued.toolheads[0].status).toBe('runout');
  });
});
