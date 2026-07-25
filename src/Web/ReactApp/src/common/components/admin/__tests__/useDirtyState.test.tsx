import { act, renderHook } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { useDirtyState } from '../useDirtyState';

interface Sample {
  name: string;
  count: number;
  tags: string[];
}

const initial: Sample = { name: 'a', count: 1, tags: ['x'] };

describe('useDirtyState', () => {
  it('starts clean', () => {
    const { result } = renderHook(() => useDirtyState(initial, { guardUnload: false }));
    expect(result.current.isDirty).toBe(false);
    expect(result.current.changedKeys).toEqual([]);
    expect(result.current.changedCount).toBe(0);
    expect(result.current.values).toEqual(initial);
    expect(result.current.original).toEqual(initial);
  });

  it('setValue flips isDirty and tracks changed keys', () => {
    const { result } = renderHook(() => useDirtyState(initial, { guardUnload: false }));
    act(() => result.current.setValue('name', 'b'));
    expect(result.current.isDirty).toBe(true);
    expect(result.current.changedKeys).toEqual(['name']);
    expect(result.current.values.name).toBe('b');
    expect(result.current.original.name).toBe('a');
  });

  it('setValue back to original clears dirty', () => {
    const { result } = renderHook(() => useDirtyState(initial, { guardUnload: false }));
    act(() => result.current.setValue('name', 'b'));
    act(() => result.current.setValue('name', 'a'));
    expect(result.current.isDirty).toBe(false);
    expect(result.current.changedKeys).toEqual([]);
  });

  it('setValues merges multiple fields at once', () => {
    const { result } = renderHook(() => useDirtyState(initial, { guardUnload: false }));
    act(() => result.current.setValues({ name: 'z', count: 9 }));
    expect(result.current.changedKeys.sort()).toEqual(['count', 'name']);
    expect(result.current.changedCount).toBe(2);
  });

  it('replaceValues swaps the working set but keeps original for comparison', () => {
    const { result } = renderHook(() => useDirtyState(initial, { guardUnload: false }));
    act(() => result.current.replaceValues({ name: 'z', count: 2, tags: [] }));
    expect(result.current.values).toEqual({ name: 'z', count: 2, tags: [] });
    expect(result.current.original).toEqual(initial);
    expect(result.current.isDirty).toBe(true);
  });

  it('reset reverts working values to original', () => {
    const { result } = renderHook(() => useDirtyState(initial, { guardUnload: false }));
    act(() => result.current.setValue('count', 42));
    act(() => result.current.reset());
    expect(result.current.values).toEqual(initial);
    expect(result.current.isDirty).toBe(false);
  });

  it('markPristine adopts current values as the new baseline', () => {
    const { result } = renderHook(() => useDirtyState(initial, { guardUnload: false }));
    act(() => result.current.setValue('name', 'saved'));
    act(() => result.current.markPristine());
    expect(result.current.isDirty).toBe(false);
    expect(result.current.original.name).toBe('saved');
  });

  it('markPristine(next) adopts an explicit baseline (post-fetch)', () => {
    const { result } = renderHook(() => useDirtyState(initial, { guardUnload: false }));
    const server: Sample = { name: 'server', count: 100, tags: ['s'] };
    act(() => result.current.markPristine(server));
    expect(result.current.isDirty).toBe(false);
    expect(result.current.values).toEqual(server);
    expect(result.current.original).toEqual(server);
  });

  it('uses structural equality for arrays', () => {
    const { result } = renderHook(() => useDirtyState(initial, { guardUnload: false }));
    act(() => result.current.setValue('tags', ['x']));
    expect(result.current.isDirty).toBe(false);
    act(() => result.current.setValue('tags', ['y']));
    expect(result.current.isDirty).toBe(true);
    expect(result.current.changedKeys).toEqual(['tags']);
  });

  it('respects a custom isEqual', () => {
    const isEqual = vi.fn((a: unknown, b: unknown) => String(a).toLowerCase() === String(b).toLowerCase());
    const { result } = renderHook(() => useDirtyState({ name: 'a' }, { guardUnload: false, isEqual }));
    act(() => result.current.setValue('name', 'A'));
    expect(isEqual).toHaveBeenCalled();
    expect(result.current.isDirty).toBe(false);
  });
});

describe('useDirtyState — beforeunload guard', () => {
  let addSpy: ReturnType<typeof vi.spyOn>;
  let removeSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    addSpy = vi.spyOn(window, 'addEventListener');
    removeSpy = vi.spyOn(window, 'removeEventListener');
  });
  afterEach(() => {
    addSpy.mockRestore();
    removeSpy.mockRestore();
  });

  const beforeunloadCalls = (spy: ReturnType<typeof vi.spyOn>) =>
    spy.mock.calls.filter(c => c[0] === 'beforeunload');

  it('does not install listener while clean (guardUnload=true)', () => {
    renderHook(() => useDirtyState(initial));
    expect(beforeunloadCalls(addSpy)).toHaveLength(0);
  });

  it('installs listener when dirty and removes it when clean again', () => {
    const { result } = renderHook(() => useDirtyState(initial));
    act(() => result.current.setValue('name', 'b'));
    expect(beforeunloadCalls(addSpy)).toHaveLength(1);
    act(() => result.current.reset());
    expect(beforeunloadCalls(removeSpy)).toHaveLength(1);
  });

  it('never installs listener when guardUnload=false', () => {
    const { result } = renderHook(() => useDirtyState(initial, { guardUnload: false }));
    act(() => result.current.setValue('name', 'b'));
    expect(beforeunloadCalls(addSpy)).toHaveLength(0);
  });

  it('unmount removes the listener', () => {
    const { result, unmount } = renderHook(() => useDirtyState(initial));
    act(() => result.current.setValue('name', 'b'));
    expect(beforeunloadCalls(addSpy)).toHaveLength(1);
    unmount();
    expect(beforeunloadCalls(removeSpy)).toHaveLength(1);
  });

  it('handler calls preventDefault and sets returnValue', () => {
    const { result } = renderHook(() => useDirtyState(initial));
    act(() => result.current.setValue('name', 'b'));
    const call = beforeunloadCalls(addSpy).at(-1);
    expect(call).toBeDefined();
    const handler = call![1] as (e: BeforeUnloadEvent) => unknown;
    const event = {
      preventDefault: vi.fn(),
      returnValue: 'untouched',
    } as unknown as BeforeUnloadEvent;
    handler(event);
    expect(event.preventDefault).toHaveBeenCalled();
    expect(event.returnValue).toBe('');
  });
});
