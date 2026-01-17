/**
 * Breadcrumb navigation component
 * Shows hierarchy and allows navigation between levels
 */
import React from 'react';
import { Link } from 'react-router-dom';
import { ChevronRightIcon } from '@/common/components/icons/MdiIcons';

export interface BreadcrumbItem {
  label: string;
  href?: string;
  current?: boolean;
}

interface BreadcrumbsProps {
  items: BreadcrumbItem[];
  className?: string;
}

export const Breadcrumbs: React.FC<BreadcrumbsProps> = ({ items, className = '' }) => {
  return (
    <nav aria-label="Breadcrumb" className={`flex items-center gap-2 text-sm text-pf-text-secondary ${className}`}>
      <ol className="flex items-center gap-2">
        {items.map((item, index) => (
          <li key={index} className="flex items-center gap-2">
            {item.href && !item.current ? (
              <Link
                to={item.href}
                className="text-pf-accent hover:text-pf-accent-hover transition-colors focus:outline-none focus-visible:ring-2 focus-visible:ring-pf-accent rounded px-1"
              >
                {item.label}
              </Link>
            ) : (
              <span className={item.current ? 'text-pf-text-primary font-medium' : ''}>
                {item.label}
              </span>
            )}
            
            {index < items.length - 1 && (
              <ChevronRightIcon className="w-4 h-4 flex-shrink-0 text-pf-border" />
            )}
          </li>
        ))}
      </ol>
    </nav>
  );
};
