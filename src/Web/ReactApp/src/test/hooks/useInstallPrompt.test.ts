import '@testing-library/jest-dom';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { useInstallPrompt } from '@/common/hooks/useInstallPrompt';

interface BeforeInstallPromptEvent extends Event {
  prompt: () => Promise<void>;
  userChoice: Promise<{ outcome: 'accepted' | 'dismissed' }>;
}

describe('useInstallPrompt', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
  });

  it('returns null canInstall when no beforeinstallprompt event', () => {
    const { result } = renderHook(() => useInstallPrompt());

    expect(result.current.canInstall).toBe(false);
    expect(result.current.promptInstall).toBeDefined();
    expect(result.current.dismiss).toBeDefined();
  });

  it('captures beforeinstallprompt event and sets canInstall to true', async () => {
    const { result } = renderHook(() => useInstallPrompt());

    expect(result.current.canInstall).toBe(false);

    // Simulate beforeinstallprompt event
    const mockEvent = new Event('beforeinstallprompt') as BeforeInstallPromptEvent;
    mockEvent.prompt = vi.fn().mockResolvedValue(undefined);
    mockEvent.userChoice = Promise.resolve({ outcome: 'accepted' as const });

    window.dispatchEvent(mockEvent);

    await waitFor(() => {
      expect(result.current.canInstall).toBe(true);
    });
  });

  it('respects 7-day dismissal cooldown from localStorage', () => {
    // Set dismissed date to 3 days ago (within cooldown)
    const threeDaysAgo = new Date();
    threeDaysAgo.setDate(threeDaysAgo.getDate() - 3);
    localStorage.setItem('pwa-install-dismissed', threeDaysAgo.toISOString());

    const { result } = renderHook(() => useInstallPrompt());

    // Dispatch event
    const mockEvent = new Event('beforeinstallprompt') as BeforeInstallPromptEvent;
    mockEvent.prompt = vi.fn().mockResolvedValue(undefined);
    mockEvent.userChoice = Promise.resolve({ outcome: 'accepted' as const });
    window.dispatchEvent(mockEvent);

    // canInstall should still be false due to cooldown
    expect(result.current.canInstall).toBe(false);
  });

  it('allows prompt after 7-day cooldown expires', async () => {
    // Set dismissed date to 8 days ago (outside cooldown)
    const eightDaysAgo = new Date();
    eightDaysAgo.setDate(eightDaysAgo.getDate() - 8);
    localStorage.setItem('pwa-install-dismissed', eightDaysAgo.toISOString());

    const { result } = renderHook(() => useInstallPrompt());

    // Dispatch event
    const mockEvent = new Event('beforeinstallprompt') as BeforeInstallPromptEvent;
    mockEvent.prompt = vi.fn().mockResolvedValue(undefined);
    mockEvent.userChoice = Promise.resolve({ outcome: 'accepted' as const });
    window.dispatchEvent(mockEvent);

    await waitFor(() => {
      expect(result.current.canInstall).toBe(true);
    });
  });

  it('promptInstall calls prompt and returns true on accepted', async () => {
    const mockPrompt = vi.fn().mockResolvedValue(undefined);
    const mockEvent = new Event('beforeinstallprompt') as BeforeInstallPromptEvent;
    mockEvent.prompt = mockPrompt;
    mockEvent.userChoice = Promise.resolve({ outcome: 'accepted' as const });

    const { result } = renderHook(() => useInstallPrompt());

    window.dispatchEvent(mockEvent);

    await waitFor(() => {
      expect(result.current.canInstall).toBe(true);
    });

    const installResult = await result.current.promptInstall();

    expect(mockPrompt).toHaveBeenCalled();
    expect(installResult).toBe(true);
  });

  it('promptInstall calls prompt and returns false on dismissed', async () => {
    const mockPrompt = vi.fn().mockResolvedValue(undefined);
    const mockEvent = new Event('beforeinstallprompt') as BeforeInstallPromptEvent;
    mockEvent.prompt = mockPrompt;
    // Create a promise that resolves synchronously for testing
    Object.defineProperty(mockEvent, 'userChoice', {
      get: () => Promise.resolve({ outcome: 'dismissed' as const })
    });

    const { result } = renderHook(() => useInstallPrompt());

    window.dispatchEvent(mockEvent);

    await waitFor(() => {
      expect(result.current.canInstall).toBe(true);
    });

    const installResult = await result.current.promptInstall();

    expect(mockPrompt).toHaveBeenCalled();
    expect(installResult).toBe(false);
  });

  it('promptInstall sets dismissal timestamp on dismissed', async () => {
    const mockPrompt = vi.fn().mockResolvedValue(undefined);
    const mockEvent = new Event('beforeinstallprompt') as BeforeInstallPromptEvent;
    mockEvent.prompt = mockPrompt;
    Object.defineProperty(mockEvent, 'userChoice', {
      get: () => Promise.resolve({ outcome: 'dismissed' as const })
    });

    const { result } = renderHook(() => useInstallPrompt());

    window.dispatchEvent(mockEvent);

    await waitFor(() => {
      expect(result.current.canInstall).toBe(true);
    });

    const beforeDismiss = new Date().getTime();
    await result.current.promptInstall();

    // Check localStorage directly (state update may not trigger re-render in test)
    const dismissedTimestamp = localStorage.getItem('pwa-install-dismissed');
    expect(dismissedTimestamp).toBeDefined();
    expect(new Date(dismissedTimestamp!).getTime()).toBeGreaterThanOrEqual(beforeDismiss);
  });

  it('promptInstall returns false when no prompt available', async () => {
    const { result } = renderHook(() => useInstallPrompt());

    // No event dispatched, so no prompt available
    const installResult = await result.current.promptInstall();

    expect(installResult).toBe(false);
  });

  it('dismiss sets localStorage timestamp', async () => {
    const mockEvent = new Event('beforeinstallprompt') as BeforeInstallPromptEvent;
    mockEvent.prompt = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(mockEvent, 'userChoice', {
      get: () => Promise.resolve({ outcome: 'accepted' as const })
    });

    const { result } = renderHook(() => useInstallPrompt());

    window.dispatchEvent(mockEvent);

    await waitFor(() => {
      expect(result.current.canInstall).toBe(true);
    });

    const beforeDismiss = new Date().getTime();
    result.current.dismiss();

    // Check localStorage directly
    const dismissedTimestamp = localStorage.getItem('pwa-install-dismissed');
    expect(dismissedTimestamp).toBeDefined();
    expect(new Date(dismissedTimestamp!).getTime()).toBeGreaterThanOrEqual(beforeDismiss);
  });

  it('cleans up event listener on unmount', () => {
    const removeEventListenerSpy = vi.spyOn(window, 'removeEventListener');

    const { unmount } = renderHook(() => useInstallPrompt());

    unmount();

    expect(removeEventListenerSpy).toHaveBeenCalledWith('beforeinstallprompt', expect.any(Function));

    removeEventListenerSpy.mockRestore();
  });

  it('handles missing localStorage gracefully', () => {
    // Remove pwa-install-dismissed from localStorage
    localStorage.removeItem('pwa-install-dismissed');

    const { result } = renderHook(() => useInstallPrompt());

    // Should initialize without errors
    expect(result.current.canInstall).toBe(false);
  });

  it('handles invalid date in localStorage gracefully', () => {
    // Set invalid date in localStorage
    localStorage.setItem('pwa-install-dismissed', 'invalid-date');

    const { result } = renderHook(() => useInstallPrompt());

    // Should initialize without errors (treats invalid date as dismissed recently)
    expect(result.current.canInstall).toBe(false);
  });

  it('promptInstall returns true on acceptance', async () => {
    const mockPrompt = vi.fn().mockResolvedValue(undefined);
    const mockEvent = new Event('beforeinstallprompt') as BeforeInstallPromptEvent;
    mockEvent.prompt = mockPrompt;
    Object.defineProperty(mockEvent, 'userChoice', {
      get: () => Promise.resolve({ outcome: 'accepted' as const })
    });

    const { result } = renderHook(() => useInstallPrompt());

    window.dispatchEvent(mockEvent);

    await waitFor(() => {
      expect(result.current.canInstall).toBe(true);
    });

    const installResult = await result.current.promptInstall();

    expect(installResult).toBe(true);
    expect(mockPrompt).toHaveBeenCalled();
  });
});
