import { describe, it, expect } from 'vitest';
import {
  computeStats,
  collectLocationIds,
  findNode,
} from '../hooks/useLocationDashboard';
import type { LocationTreeNode, LocationSubtreePrinter } from '@/types/api';

const makeNode = (overrides: Partial<LocationTreeNode> = {}): LocationTreeNode => ({
  id: 'n1',
  name: 'Node 1',
  parentId: null,
  path: '/Node 1',
  depth: 0,
  sortOrder: 0,
  printerCount: 0,
  totalPrinterCount: 0,
  children: [],
  ...overrides,
});

const makePrinter = (overrides: Partial<LocationSubtreePrinter> = {}): LocationSubtreePrinter => ({
  printerId: 'p1',
  printerName: 'Printer 1',
  locationId: 'loc-1',
  locationName: 'Rack 1',
  isOnline: true,
  status: 'Idle',
  currentJobName: null,
  ...overrides,
});

describe('computeStats', () => {
  it('returns zeros for empty array', () => {
    const stats = computeStats([]);
    expect(stats).toEqual({
      totalPrinters: 0,
      online: 0,
      offline: 0,
      attention: 0,
      printing: 0,
      idle: 0,
      activeJobs: 0,
    });
  });

  it('computes correct counts for mixed statuses', () => {
    const printers = [
      makePrinter({ printerId: 'printing', status: 'Printing', currentJobName: 'gearbox.gcode' }),
      makePrinter({ printerId: 'idle', status: 'Idle' }),
      makePrinter({ printerId: 'paused', status: 'Paused' }),
      makePrinter({ printerId: 'offline', isOnline: false, status: 'Disconnected' }),
    ];

    const stats = computeStats(printers);
    expect(stats.totalPrinters).toBe(4);
    expect(stats.online).toBe(3);
    expect(stats.offline).toBe(1);
    expect(stats.attention).toBe(2);
    expect(stats.printing).toBe(1);
    expect(stats.idle).toBe(1);
    expect(stats.activeJobs).toBe(1);
  });

  it('counts canonical Idle status as idle', () => {
    const printers = [makePrinter({ status: 'Idle' })];
    const stats = computeStats(printers);
    expect(stats.idle).toBe(1);
  });
});

describe('collectLocationIds', () => {
  it('returns single id for leaf node', () => {
    const node = makeNode({ id: 'leaf' });
    expect(collectLocationIds(node)).toEqual(['leaf']);
  });

  it('returns all descendant ids', () => {
    const node = makeNode({
      id: 'root',
      children: [
        makeNode({ id: 'child1', children: [makeNode({ id: 'grandchild' })] }),
        makeNode({ id: 'child2' }),
      ],
    });
    const ids = collectLocationIds(node);
    expect(ids).toContain('root');
    expect(ids).toContain('child1');
    expect(ids).toContain('child2');
    expect(ids).toContain('grandchild');
    expect(ids.length).toBe(4);
  });
});

describe('findNode', () => {
  const tree: LocationTreeNode[] = [
    makeNode({
      id: 'a',
      name: 'A',
      children: [
        makeNode({ id: 'b', name: 'B' }),
        makeNode({ id: 'c', name: 'C', children: [makeNode({ id: 'd', name: 'D' })] }),
      ],
    }),
  ];

  it('finds root node', () => {
    expect(findNode(tree, 'a')?.name).toBe('A');
  });

  it('finds deeply nested node', () => {
    expect(findNode(tree, 'd')?.name).toBe('D');
  });

  it('returns undefined for missing id', () => {
    expect(findNode(tree, 'missing')).toBeUndefined();
  });

  it('returns undefined for empty tree', () => {
    expect(findNode([], 'a')).toBeUndefined();
  });
});
