import { describe, expect, it } from 'vitest';
import { canMove, canSetStep, canUseManualMove, getPrinterSupport } from '../printerSupport';

describe('printerSupport movement gating (#1909)', () => {
  const support = getPrinterSupport();

  describe('canMove', () => {
    it('allows movement when the printer is online, idle, and Klippy is not shutdown', () => {
      expect(canMove({ isOnline: true, isPrinting: false, isShutdown: false, support })).toBe(true);
    });

    it('disables movement (Home/jog) while Klippy is shutdown, even if otherwise idle and online', () => {
      expect(canMove({ isOnline: true, isPrinting: false, isShutdown: true, support })).toBe(false);
    });

    it('treats an omitted isShutdown flag as not shut down (backward compatible default)', () => {
      expect(canMove({ isOnline: true, isPrinting: false, support })).toBe(true);
    });

    it('still disables movement while printing regardless of shutdown state', () => {
      expect(canMove({ isOnline: true, isPrinting: true, isShutdown: false, support })).toBe(false);
    });
  });

  describe('canUseManualMove', () => {
    it('disables jog/manual-move controls while Klippy is shutdown', () => {
      expect(canUseManualMove({ isOnline: true, isPrinting: false, isShutdown: true, support })).toBe(false);
    });

    it('allows manual move when not shut down', () => {
      expect(canUseManualMove({ isOnline: true, isPrinting: false, isShutdown: false, support })).toBe(true);
    });
  });

  describe('canSetStep', () => {
    it('disables step selection while Klippy is shutdown', () => {
      expect(canSetStep({ isOnline: true, isShutdown: true, support })).toBe(false);
    });

    it('allows step selection when not shut down', () => {
      expect(canSetStep({ isOnline: true, isShutdown: false, support })).toBe(true);
    });
  });
});
