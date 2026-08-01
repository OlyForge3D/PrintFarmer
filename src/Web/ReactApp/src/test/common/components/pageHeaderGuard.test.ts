import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import {
  registerPageHeader,
  mountedPageHeaderCount,
  resetPageHeaderGuard,
} from '@/common/components/pageHeaderGuard';

describe('pageHeaderGuard', () => {
  beforeEach(() => {
    resetPageHeaderGuard();
  });

  afterEach(() => {
    vi.restoreAllMocks();
    resetPageHeaderGuard();
  });

  it('stays quiet while only one page header is mounted', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});

    const release = registerPageHeader('Admin Console');
    expect(mountedPageHeaderCount()).toBe(1);
    expect(warn).not.toHaveBeenCalled();

    release();
    expect(mountedPageHeaderCount()).toBe(0);
  });

  it('warns when a second page header mounts alongside the first', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});

    registerPageHeader('Admin Console');
    registerPageHeader('User Management');

    expect(warn).toHaveBeenCalledTimes(1);
    const [message] = warn.mock.calls[0] as [string];
    expect(message).toContain('"Admin Console"');
    expect(message).toContain('"User Management"');
    expect(message).toContain('embedded');
  });

  it('warns at most once so a broken route does not flood the console', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});

    registerPageHeader('A');
    registerPageHeader('B');
    registerPageHeader('C');

    expect(warn).toHaveBeenCalledTimes(1);
  });

  it('releases only the instance that registered, even for duplicate titles', () => {
    vi.spyOn(console, 'warn').mockImplementation(() => {});

    const first = registerPageHeader('Settings');
    registerPageHeader('Settings');
    expect(mountedPageHeaderCount()).toBe(2);

    first();
    expect(mountedPageHeaderCount()).toBe(1);
  });

  it('survives a mount/unmount/mount cycle without warning, as StrictMode produces', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});

    const release = registerPageHeader('Printers');
    release();
    registerPageHeader('Printers');

    expect(mountedPageHeaderCount()).toBe(1);
    expect(warn).not.toHaveBeenCalled();
  });
});
