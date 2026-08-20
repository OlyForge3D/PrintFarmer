import '@testing-library/jest-dom';
import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { Tabs } from '@/common/components/ui/Tabs';

describe('Tabs', () => {
  describe('Basic Rendering', () => {
    it('should render tabs with default active tab', () => {
      render(
        <Tabs defaultTab="tab1">
          <Tabs.List>
            <Tabs.Tab id="tab1">Tab 1</Tabs.Tab>
            <Tabs.Tab id="tab2">Tab 2</Tabs.Tab>
          </Tabs.List>
          <Tabs.Panels>
            <Tabs.Panel id="tab1">Content 1</Tabs.Panel>
            <Tabs.Panel id="tab2">Content 2</Tabs.Panel>
          </Tabs.Panels>
        </Tabs>
      );

      expect(screen.getByText('Tab 1')).toBeInTheDocument();
      expect(screen.getByText('Tab 2')).toBeInTheDocument();
      expect(screen.getByText('Content 1')).toBeInTheDocument();
    });

    it('should show only the active panel', () => {
      render(
        <Tabs defaultTab="tab1">
          <Tabs.List>
            <Tabs.Tab id="tab1">Tab 1</Tabs.Tab>
            <Tabs.Tab id="tab2">Tab 2</Tabs.Tab>
          </Tabs.List>
          <Tabs.Panels>
            <Tabs.Panel id="tab1">Content 1</Tabs.Panel>
            <Tabs.Panel id="tab2">Content 2</Tabs.Panel>
          </Tabs.Panels>
        </Tabs>
      );

      // Content 1 should be visible
      expect(screen.getByText('Content 1')).toBeInTheDocument();
      // Content 2 should not be visible (hidden)
      expect(screen.queryByText('Content 2')).not.toBeInTheDocument();
    });
  });

  describe('Tab Switching', () => {
    it('should switch tabs when tab is clicked', () => {
      render(
        <Tabs defaultTab="tab1">
          <Tabs.List>
            <Tabs.Tab id="tab1">Tab 1</Tabs.Tab>
            <Tabs.Tab id="tab2">Tab 2</Tabs.Tab>
          </Tabs.List>
          <Tabs.Panels>
            <Tabs.Panel id="tab1">Content 1</Tabs.Panel>
            <Tabs.Panel id="tab2">Content 2</Tabs.Panel>
          </Tabs.Panels>
        </Tabs>
      );

      // Click Tab 2
      fireEvent.click(screen.getByText('Tab 2'));

      // Content 2 should now be visible
      expect(screen.getByText('Content 2')).toBeInTheDocument();
      // Content 1 should be hidden
      expect(screen.queryByText('Content 1')).not.toBeInTheDocument();
    });

    it('should call onTabChange when tab changes', () => {
      const onTabChange = vi.fn();
      
      render(
        <Tabs defaultTab="tab1" onTabChange={onTabChange}>
          <Tabs.List>
            <Tabs.Tab id="tab1">Tab 1</Tabs.Tab>
            <Tabs.Tab id="tab2">Tab 2</Tabs.Tab>
          </Tabs.List>
          <Tabs.Panels>
            <Tabs.Panel id="tab1">Content 1</Tabs.Panel>
            <Tabs.Panel id="tab2">Content 2</Tabs.Panel>
          </Tabs.Panels>
        </Tabs>
      );

      fireEvent.click(screen.getByText('Tab 2'));

      expect(onTabChange).toHaveBeenCalledWith('tab2');
    });
  });

  describe('Controlled Mode', () => {
    it('should work in controlled mode', () => {
      const onTabChange = vi.fn();
      
      const { rerender } = render(
        <Tabs activeTab="tab1" onTabChange={onTabChange}>
          <Tabs.List>
            <Tabs.Tab id="tab1">Tab 1</Tabs.Tab>
            <Tabs.Tab id="tab2">Tab 2</Tabs.Tab>
          </Tabs.List>
          <Tabs.Panels>
            <Tabs.Panel id="tab1">Content 1</Tabs.Panel>
            <Tabs.Panel id="tab2">Content 2</Tabs.Panel>
          </Tabs.Panels>
        </Tabs>
      );

      expect(screen.getByText('Content 1')).toBeInTheDocument();

      // Click Tab 2
      fireEvent.click(screen.getByText('Tab 2'));
      
      // Should call onTabChange
      expect(onTabChange).toHaveBeenCalledWith('tab2');

      // Rerender with new activeTab to simulate controlled update
      rerender(
        <Tabs activeTab="tab2" onTabChange={onTabChange}>
          <Tabs.List>
            <Tabs.Tab id="tab1">Tab 1</Tabs.Tab>
            <Tabs.Tab id="tab2">Tab 2</Tabs.Tab>
          </Tabs.List>
          <Tabs.Panels>
            <Tabs.Panel id="tab1">Content 1</Tabs.Panel>
            <Tabs.Panel id="tab2">Content 2</Tabs.Panel>
          </Tabs.Panels>
        </Tabs>
      );

      expect(screen.getByText('Content 2')).toBeInTheDocument();
    });
  });

  describe('Styling', () => {
    it('should apply custom className to tabs container', () => {
      render(
        <Tabs defaultTab="tab1" className="custom-tabs">
          <Tabs.List>
            <Tabs.Tab id="tab1">Tab 1</Tabs.Tab>
          </Tabs.List>
          <Tabs.Panels>
            <Tabs.Panel id="tab1">Content</Tabs.Panel>
          </Tabs.Panels>
        </Tabs>
      );

      const tabsContainer = screen.getByText('Tab 1').closest('.custom-tabs');
      expect(tabsContainer).toBeInTheDocument();
    });

    it('should indicate active tab visually', () => {
      render(
        <Tabs defaultTab="tab1">
          <Tabs.List>
            <Tabs.Tab id="tab1">Tab 1</Tabs.Tab>
            <Tabs.Tab id="tab2">Tab 2</Tabs.Tab>
          </Tabs.List>
          <Tabs.Panels>
            <Tabs.Panel id="tab1">Content 1</Tabs.Panel>
            <Tabs.Panel id="tab2">Content 2</Tabs.Panel>
          </Tabs.Panels>
        </Tabs>
      );

      // Get the actual tab button (role="tab"), not the span inside it
      const tab1Button = screen.getByRole('tab', { name: 'Tab 1' });
      // Active tab should have aria-selected="true"
      expect(tab1Button).toHaveAttribute('aria-selected', 'true');
    });
  });

  describe('Accessibility', () => {
    it('should have proper ARIA roles', () => {
      render(
        <Tabs defaultTab="tab1">
          <Tabs.List>
            <Tabs.Tab id="tab1">Tab 1</Tabs.Tab>
            <Tabs.Tab id="tab2">Tab 2</Tabs.Tab>
          </Tabs.List>
          <Tabs.Panels>
            <Tabs.Panel id="tab1">Content 1</Tabs.Panel>
            <Tabs.Panel id="tab2">Content 2</Tabs.Panel>
          </Tabs.Panels>
        </Tabs>
      );

      // Tab list should have tablist role
      expect(screen.getByRole('tablist')).toBeInTheDocument();
      
      // Tabs should have tab role
      const tabs = screen.getAllByRole('tab');
      expect(tabs).toHaveLength(2);
      
      // Tab panel should have tabpanel role
      expect(screen.getByRole('tabpanel')).toBeInTheDocument();
    });

    it('should have aria-selected on tabs', () => {
      render(
        <Tabs defaultTab="tab1">
          <Tabs.List>
            <Tabs.Tab id="tab1">Tab 1</Tabs.Tab>
            <Tabs.Tab id="tab2">Tab 2</Tabs.Tab>
          </Tabs.List>
          <Tabs.Panels>
            <Tabs.Panel id="tab1">Content 1</Tabs.Panel>
            <Tabs.Panel id="tab2">Content 2</Tabs.Panel>
          </Tabs.Panels>
        </Tabs>
      );

      const tab1 = screen.getByRole('tab', { name: 'Tab 1' });
      const tab2 = screen.getByRole('tab', { name: 'Tab 2' });

      expect(tab1).toHaveAttribute('aria-selected', 'true');
      expect(tab2).toHaveAttribute('aria-selected', 'false');
    });

    it('should support keyboard navigation', () => {
      render(
        <Tabs defaultTab="tab1">
          <Tabs.List>
            <Tabs.Tab id="tab1">Tab 1</Tabs.Tab>
            <Tabs.Tab id="tab2">Tab 2</Tabs.Tab>
          </Tabs.List>
          <Tabs.Panels>
            <Tabs.Panel id="tab1">Content 1</Tabs.Panel>
            <Tabs.Panel id="tab2">Content 2</Tabs.Panel>
          </Tabs.Panels>
        </Tabs>
      );

      // Tabs should be focusable
      const tab1 = screen.getByRole('tab', { name: 'Tab 1' });
      tab1.focus();
      expect(document.activeElement).toBe(tab1);

      // Tab switching on Enter
      const tab2 = screen.getByRole('tab', { name: 'Tab 2' });
      fireEvent.click(tab2);
      expect(screen.getByText('Content 2')).toBeInTheDocument();
    });
  });

  describe('Multiple Tabs', () => {
    it('should support more than two tabs', () => {
      render(
        <Tabs defaultTab="tab1">
          <Tabs.List>
            <Tabs.Tab id="tab1">Tab 1</Tabs.Tab>
            <Tabs.Tab id="tab2">Tab 2</Tabs.Tab>
            <Tabs.Tab id="tab3">Tab 3</Tabs.Tab>
            <Tabs.Tab id="tab4">Tab 4</Tabs.Tab>
          </Tabs.List>
          <Tabs.Panels>
            <Tabs.Panel id="tab1">Content 1</Tabs.Panel>
            <Tabs.Panel id="tab2">Content 2</Tabs.Panel>
            <Tabs.Panel id="tab3">Content 3</Tabs.Panel>
            <Tabs.Panel id="tab4">Content 4</Tabs.Panel>
          </Tabs.Panels>
        </Tabs>
      );

      expect(screen.getAllByRole('tab')).toHaveLength(4);

      // Switch to tab 4
      fireEvent.click(screen.getByText('Tab 4'));
      expect(screen.getByText('Content 4')).toBeInTheDocument();
    });
  });

  describe('Error Handling', () => {
    it('should throw error when Tab is used outside Tabs context', () => {
      // Suppress console.error for this test
      const consoleSpy = vi.spyOn(console, 'error').mockImplementation(() => {});

      expect(() => {
        render(<Tabs.Tab id="orphan">Orphan Tab</Tabs.Tab>);
      }).toThrow();

      consoleSpy.mockRestore();
    });
  });

  // Regression coverage for issue #1754: on a 375px viewport the Print Queue
  // tab strip (Print Queue / Timeline / History / Dispatch Log) had no wrap
  // or scroll affordance, so the "Dispatch Log" tab was clipped off-screen
  // and the page required horizontal scrolling. jsdom does not compute real
  // layout, so this asserts the tab list scrolls its own content horizontally
  // (instead of clipping it) rather than measuring pixel widths.
  //
  // `overflow-x-auto` is opted into per-consumer via `className` (the same
  // convention MaintenanceDashboardPage already uses) rather than forced on
  // the shared TabList by default: forcing it unconditionally would make
  // `overflow-y` resolve to `auto` too (per the CSS Overflow spec, when only
  // one axis is set to a non-`visible` value), which risks clipping the
  // active tab's `-mb-px` seam across every one of TabList's other
  // consumers. PrintQueueDashboardPage passes `overflow-x-auto` explicitly,
  // exactly as reproduced below.
  describe('Mobile control layout (issue #1754)', () => {
    it('makes the tab list horizontally scrollable instead of clipping tabs', () => {
      render(
        <Tabs defaultTab="print-queue">
          <Tabs.List aria-label="Print queue tabs" className="overflow-x-auto">
            <Tabs.Tab id="print-queue">Print Queue</Tabs.Tab>
            <Tabs.Tab id="timeline">Timeline</Tabs.Tab>
            <Tabs.Tab id="history">History</Tabs.Tab>
            <Tabs.Tab id="dispatch-log">Dispatch Log</Tabs.Tab>
          </Tabs.List>
          <Tabs.Panels>
            <Tabs.Panel id="print-queue">Queue</Tabs.Panel>
            <Tabs.Panel id="timeline">Timeline</Tabs.Panel>
            <Tabs.Panel id="history">History</Tabs.Panel>
            <Tabs.Panel id="dispatch-log">Dispatch Log</Tabs.Panel>
          </Tabs.Panels>
        </Tabs>
      );

      const tabList = screen.getByRole('tablist');
      expect(tabList).toHaveClass('overflow-x-auto');
      expect(tabList).not.toHaveClass('overflow-hidden');

      // Dispatch Log — the specific tab reported clipped off-screen — must
      // still be reachable in the accessibility tree and not shrunk below
      // its content.
      const dispatchLogTab = screen.getByRole('tab', { name: 'Dispatch Log' });
      expect(dispatchLogTab).toBeVisible();
      expect(dispatchLogTab).toHaveClass('shrink-0');
    });
  });
});
