/* eslint-disable local/pf-no-raw-html-controls */
import React, { createContext, useContext, useState } from 'react';
import clsx from 'clsx';

interface TabsContextValue {
  activeTab: string;
  setActiveTab: (id: string) => void;
}

const TabsContext = createContext<TabsContextValue | null>(null);

const useTabsContext = () => {
  const context = useContext(TabsContext);
  if (!context) {
    throw new Error('Tabs components must be used within a Tabs provider');
  }
  return context;
};

export interface TabsProps {
  defaultTab?: string;
  activeTab?: string;
  onTabChange?: (tabId: string) => void;
  children: React.ReactNode;
  className?: string;
}

export const Tabs: React.FC<TabsProps> & {
  List: typeof TabList;
  Tab: typeof Tab;
  Panels: typeof TabPanels;
  Panel: typeof TabPanel;
} = ({
  defaultTab,
  activeTab: controlledActiveTab,
  onTabChange,
  children,
  className,
}) => {
  const [internalActiveTab, setInternalActiveTab] = useState(defaultTab || '');

  const activeTab = controlledActiveTab !== undefined ? controlledActiveTab : internalActiveTab;

  const setActiveTab = (id: string) => {
    if (controlledActiveTab === undefined) {
      setInternalActiveTab(id);
    }
    onTabChange?.(id);
  };

  return (
    <TabsContext.Provider value={{ activeTab, setActiveTab }}>
      <div className={clsx('w-full', className)}>{children}</div>
    </TabsContext.Provider>
  );
};

export interface TabListProps {
  children: React.ReactNode;
  className?: string;
  'aria-label'?: string;
}

const TabList: React.FC<TabListProps> = ({ children, className, 'aria-label': ariaLabel }) => {
  return (
    <div
      className={clsx('flex items-center gap-2 bg-pf-bg-1 px-2 pt-2 pb-0', className)}
      role="tablist"
      aria-orientation="horizontal"
      aria-label={ariaLabel}
    >
      {children}
    </div>
  );
};

export interface TabProps {
  id: string;
  children: React.ReactNode;
  disabled?: boolean;
  icon?: React.ReactNode;
  className?: string;
}

const Tab: React.FC<TabProps> = ({
  id,
  children,
  disabled = false,
  icon,
  className,
}) => {
  const { activeTab, setActiveTab } = useTabsContext();
  const isActive = activeTab === id;
  const btnRef = React.useRef<HTMLButtonElement | null>(null);

  const handleKeyDown = (event: React.KeyboardEvent<HTMLButtonElement>) => {
    if (!['ArrowRight', 'ArrowLeft', 'Home', 'End'].includes(event.key)) {
      return;
    }

    const tabButtons = Array.from(
      event.currentTarget.parentElement?.querySelectorAll<HTMLButtonElement>('[role="tab"]:not(:disabled)') ?? [],
    );

    const currentIndex = tabButtons.indexOf(event.currentTarget);
    if (currentIndex === -1 || tabButtons.length === 0) {
      return;
    }

    event.preventDefault();

    let nextIndex = currentIndex;
    if (event.key === 'ArrowRight') {
      nextIndex = (currentIndex + 1) % tabButtons.length;
    } else if (event.key === 'ArrowLeft') {
      nextIndex = (currentIndex - 1 + tabButtons.length) % tabButtons.length;
    } else if (event.key === 'Home') {
      nextIndex = 0;
    } else if (event.key === 'End') {
      nextIndex = tabButtons.length - 1;
    }

    const nextTab = tabButtons[nextIndex];
    const nextId = nextTab.dataset.tabId;
    if (!nextId) {
      return;
    }

    setActiveTab(nextId);
    nextTab.focus();
  };

  return (
    <button
      ref={btnRef}
      type="button"
      role="tab"
      id={`tab-${id}`}
      data-tab-id={id}
      aria-selected={isActive}
      aria-controls={`panel-${id}`}
      tabIndex={disabled ? -1 : isActive ? 0 : -1}
      disabled={disabled}
      onClick={() => !disabled && setActiveTab(id)}
      onKeyDown={handleKeyDown}
      onMouseUp={() => {
        if (btnRef.current && document.activeElement === btnRef.current) {
          btnRef.current.blur();
        }
      }}
      className={clsx(
        'px-4 py-2 text-sm font-medium transition-colors rounded-none',
        'focus:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent focus-visible:ring-inset',
        isActive
          ? 'relative z-20 -mb-px border-l-0 border-t border-r-0 border-b-0 border-pf-border bg-pf-bg-0 text-pf-text-primary rounded-none'
          : 'relative z-0 border-l-0 border-t border-r-0 border-b-0 border-pf-border text-pf-text-secondary rounded-none',
        disabled && 'cursor-not-allowed opacity-50',
        className,
      )}
    >
      <span className="inline-flex items-center gap-2">
        {icon}
        {children}
      </span>
    </button>
  );
};

export interface TabPanelsProps {
  children: React.ReactNode;
  className?: string;
}

const TabPanels: React.FC<TabPanelsProps> = ({ children, className }) => {
  return (
    <div className={clsx('border border-pf-border bg-pf-bg-0 p-4 -mt-px', className)}>
      {children}
    </div>
  );
};

export interface TabPanelProps {
  id: string;
  children: React.ReactNode;
  className?: string;
}

const TabPanel: React.FC<TabPanelProps> = ({ id, children, className }) => {
  const { activeTab } = useTabsContext();
  const isActive = activeTab === id;

  if (!isActive) return null;

  return (
    <div
      role="tabpanel"
      id={`panel-${id}`}
      aria-labelledby={`tab-${id}`}
      className={className}
    >
      {children}
    </div>
  );
};

Tabs.List = TabList;
Tabs.Tab = Tab;
Tabs.Panels = TabPanels;
Tabs.Panel = TabPanel;

export default Tabs;
