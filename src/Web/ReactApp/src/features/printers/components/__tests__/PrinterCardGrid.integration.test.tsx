import { act, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Printer } from '@/types/api';
import { PrinterCardGrid } from '@/features/printers/components/PrinterCardGrid';

const SCROLLER_HEIGHT_PX = 600;
const GRID_OFFSET_PX = 200;
const GRID_WIDTH_PX = 1000;
const CARD_HEIGHT_PX = 320;

let scrollToMock: ReturnType<typeof vi.fn>;

function createPrinters(count: number): Printer[] {
  return Array.from({ length: count }, (_, index) => ({
    id: `printer-${index}`,
    name: `Printer ${index}`,
    backend: 'Moonraker',
    isOnline: true,
    isEnabled: true,
    state: 'Idle',
  })) as Printer[];
}

function rect(top: number, width: number, height: number): DOMRect {
  return {
    x: 0,
    y: top,
    top,
    right: width,
    bottom: top + height,
    left: 0,
    width,
    height,
    toJSON: () => ({}),
  };
}

describe('PrinterCardGrid with the real TanStack element virtualizer', () => {
  beforeEach(() => {
    vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockImplementation(function (this: HTMLElement) {
      return this.hasAttribute('data-main-content') || this.dataset.testid === 'virtualized-printer-grid'
        ? GRID_WIDTH_PX
        : 0;
    });
    vi.spyOn(HTMLElement.prototype, 'offsetWidth', 'get').mockImplementation(function (this: HTMLElement) {
      return this.hasAttribute('data-main-content') ? GRID_WIDTH_PX : 0;
    });
    vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockImplementation(function (this: HTMLElement) {
      if (this.hasAttribute('data-main-content')) return SCROLLER_HEIGHT_PX;
      if (this.dataset.printerId) return CARD_HEIGHT_PX;
      return Number.parseFloat(this.style.height) || 0;
    });
    vi.spyOn(HTMLElement.prototype, 'clientHeight', 'get').mockImplementation(function (this: HTMLElement) {
      return this.hasAttribute('data-main-content') ? SCROLLER_HEIGHT_PX : 0;
    });
    vi.spyOn(HTMLElement.prototype, 'scrollHeight', 'get').mockImplementation(function (this: HTMLElement) {
      return this.hasAttribute('data-main-content') ? 7200 : 0;
    });
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(function (this: HTMLElement) {
      if (this.hasAttribute('data-main-content')) {
        return rect(0, GRID_WIDTH_PX, SCROLLER_HEIGHT_PX);
      }
      if (this.dataset.testid === 'virtualized-printer-grid') {
        const main = this.closest<HTMLElement>('[data-main-content]');
        return rect(GRID_OFFSET_PX - (main?.scrollTop ?? 0), GRID_WIDTH_PX, 0);
      }
      if (this.dataset.printerId) return rect(0, 288, CARD_HEIGHT_PX);
      const measuredHeight = Number.parseFloat(this.style.height) || 0;
      return rect(0, 1, measuredHeight);
    });

    scrollToMock = vi.fn(function (this: HTMLElement, options: ScrollToOptions) {
      this.scrollTop = options.top ?? this.scrollTop;
      this.dispatchEvent(new Event('scroll'));
    });
    Object.defineProperty(HTMLElement.prototype, 'scrollTo', {
      configurable: true,
      value: scrollToMock,
    });
  });

  afterEach(() => {
    vi.restoreAllMocks();
    delete (HTMLElement.prototype as Partial<HTMLElement>).scrollTo;
  });

  it('virtualizes against main[data-main-content] and keeps the row-zero tour anchor stable', async () => {
    const printers = createPrinters(60);
    const { container } = render(
      <main data-main-content style={{ height: SCROLLER_HEIGHT_PX, overflowY: 'auto' }}>
        <PrinterCardGrid
          printers={printers}
          mode="compact"
          renderPrinter={(printer) => <button type="button">Open {printer.name}</button>}
        />
      </main>,
    );

    await waitFor(() => {
      expect(screen.getAllByRole('button').length).toBeLessThan(printers.length);
      expect(screen.getByRole('button', { name: 'Open Printer 0' })).toBeInTheDocument();
    });

    const main = container.querySelector<HTMLElement>('[data-main-content]')!;
    act(() => {
      main.scrollTop = 2200;
      main.dispatchEvent(new Event('scroll'));
    });

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Open Printer 18' })).toBeInTheDocument();
    });
    expect(container.querySelector('[data-tour="printers-card"] [type="button"]'))
      .toHaveTextContent('Open Printer 0');
  });

  it('deep-link scrolling writes to the Layout main scroller', async () => {
    const printers = createPrinters(60);
    const { container } = render(
      <main data-main-content style={{ height: SCROLLER_HEIGHT_PX, overflowY: 'auto' }}>
        <PrinterCardGrid
          printers={printers}
          mode="compact"
          activePrinterId="printer-55"
          renderPrinter={(printer) => <button type="button">Open {printer.name}</button>}
        />
      </main>,
    );

    const main = container.querySelector<HTMLElement>('[data-main-content]')!;
    await waitFor(() => expect(main.scrollTop).toBeGreaterThan(0));
    expect(scrollToMock.mock.instances).toContain(main);
  });
});
