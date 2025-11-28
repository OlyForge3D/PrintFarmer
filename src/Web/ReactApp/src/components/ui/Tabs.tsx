import React, { createContext, useContext, useState } from 'react';
import clsx from 'clsx';

// Context for Tabs state
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

// Main Tabs container
export interface TabsProps {
  /** Default active tab ID */
  defaultTab?: string;
  /** Controlled active tab ID */
  activeTab?: string;
  /** Callback when tab changes */
  onTabChange?: (tabId: string) => void;
  /** Tab content */
  children: React.ReactNode;
  /** Additional className */
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

// Tab List container
export interface TabListProps {
  children: React.ReactNode;
  className?: string;
}

const TabList: React.FC<TabListProps> = ({ children, className }) => {
  return (
    <div
      className={clsx(
        'flex border-b border-pf-border',
        className
      )}
      role="tablist"
    >
      {children}
    </div>
  );
};

// Individual Tab button
export interface TabProps {
  /** Unique identifier for this tab */
  id: string;
  /** Tab label */
  children: React.ReactNode;
  /** Whether the tab is disabled */
  disabled?: boolean;
  /** Icon to show before label */
  icon?: React.ReactNode;
  /** Additional className */
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

  return (
    <button
      type="button"
      role="tab"
      aria-selected={isActive}
      aria-controls={`panel-${id}`}
      id={`tab-${id}`}
      disabled={disabled}
      onClick={() => !disabled && setActiveTab(id)}
      className={clsx(
        'px-4 py-2 text-sm font-medium transition-colors',
        'border-b-2 -mb-px',
        'focus:outline-none focus:ring-2 focus:ring-pf-accent focus:ring-inset',
        isActive
          ? 'border-pf-accent text-pf-accent'
          : 'border-transparent text-pf-text-secondary hover:text-pf-text-primary hover:border-pf-border',
        disabled && 'opacity-50 cursor-not-allowed',
        className
      )}
    >
      <span className="inline-flex items-center gap-2">
        {icon}
        {children}
      </span>
    </button>
  );
};

// Tab Panels container
export interface TabPanelsProps {
  children: React.ReactNode;
  className?: string;
}

const TabPanels: React.FC<TabPanelsProps> = ({ children, className }) => {
  return <div className={clsx('mt-4', className)}>{children}</div>;
};

// Individual Tab Panel
export interface TabPanelProps {
  /** Must match the corresponding Tab id */
  id: string;
  /** Panel content */
  children: React.ReactNode;
  /** Additional className */
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

// Attach sub-components
Tabs.List = TabList;
Tabs.Tab = Tab;
Tabs.Panels = TabPanels;
Tabs.Panel = TabPanel;

export default Tabs;
