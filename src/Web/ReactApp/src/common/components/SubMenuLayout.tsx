import React from 'react';
import { NavLink, Outlet } from 'react-router';

interface SubMenuItem {
  name: string;
  href: string;
  icon?: React.ComponentType<{ className?: string }>;
}

interface SubMenuLayoutProps {
  title: string;
  items: SubMenuItem[];
  children?: React.ReactNode;
}

export function SubMenuLayout({ title, items, children }: SubMenuLayoutProps) {
  return (
    <div className="flex flex-1 overflow-hidden">
      {/* Submenu Panel */}
      <aside className="w-56 bg-pf-bg-1 border-r border-pf-border overflow-y-auto">
        <div className="px-4 py-2 border-b border-pf-border sticky top-0 bg-pf-bg-1 z-10">
          <h2 className="text-lg font-semibold text-pf-text-primary">{title}</h2>
        </div>
        <nav className="space-y-1">
          {items.map((item) => (
            <NavLink
              key={item.href}
              to={item.href}
              className={({ isActive }: { isActive: boolean }) =>
                `flex items-center px-4 py-2 text-sm transition-colors focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent ${
                  isActive
                    ? 'bg-pf-bg-2 text-pf-text-primary border-r-2 border-pf-accent'
                    : 'text-pf-text-secondary hover:text-pf-text-primary hover:bg-pf-bg-2'
                }`
              }
            >
              {item.icon && <item.icon className="mr-2 h-4 w-4 shrink-0" />}
              <span>{item.name}</span>
            </NavLink>
          ))}
        </nav>
      </aside>

      {/* Content Area */}
      <main className="flex-1 overflow-y-auto">
        <div className="pt-0 pr-0 pl-2">
          {children || <Outlet />}
        </div>
      </main>
    </div>
  );
}
