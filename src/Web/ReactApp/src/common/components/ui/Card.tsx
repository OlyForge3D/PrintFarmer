import React from 'react';
import clsx from 'clsx';

// Main Card container
export interface CardProps {
  /** Card content */
  children: React.ReactNode;
  /** Additional className */
  className?: string;
  /** Whether to add hover effect */
  hoverable?: boolean;
  /** Click handler (makes card interactive) */
  onClick?: () => void;
}

export const Card: React.FC<CardProps> & {
  Header: typeof CardHeader;
  Body: typeof CardBody;
  Footer: typeof CardFooter;
} = ({ children, className, hoverable = false, onClick }) => {
  const isInteractive = !!onClick || hoverable;

  return (
    <div
      className={clsx(
        'bg-pf-panel border border-pf-border rounded-lg overflow-hidden',
        isInteractive && 'transition-all duration-200',
        hoverable && 'hover:border-pf-accent/50 hover:shadow-md',
        onClick && 'cursor-pointer',
        className
      )}
      onClick={onClick}
      role={onClick ? 'button' : undefined}
      tabIndex={onClick ? 0 : undefined}
      onKeyDown={
        onClick
          ? (e) => {
              if (e.key === 'Enter' || e.key === ' ') {
                e.preventDefault();
                onClick();
              }
            }
          : undefined
      }
    >
      {children}
    </div>
  );
};

// Card Header
export interface CardHeaderProps {
  /** Header content */
  children: React.ReactNode;
  /** Additional className */
  className?: string;
  /** Optional action buttons/elements */
  actions?: React.ReactNode;
}

const CardHeader: React.FC<CardHeaderProps> = ({
  children,
  className,
  actions,
}) => {
  return (
    <div
      className={clsx(
        'px-4 py-3 border-b border-pf-border bg-pf-bg-1',
        'flex items-center justify-between',
        className
      )}
    >
      <div className="font-semibold text-pf-text-primary">{children}</div>
      {actions && <div className="flex items-center gap-2">{actions}</div>}
    </div>
  );
};

// Card Body
export interface CardBodyProps {
  /** Body content */
  children: React.ReactNode;
  /** Additional className */
  className?: string;
  /** Remove default padding */
  noPadding?: boolean;
}

const CardBody: React.FC<CardBodyProps> = ({
  children,
  className,
  noPadding = false,
}) => {
  return (
    <div className={clsx(!noPadding && 'p-4', className)}>{children}</div>
  );
};

// Card Footer
export interface CardFooterProps {
  /** Footer content */
  children: React.ReactNode;
  /** Additional className */
  className?: string;
}

const CardFooter: React.FC<CardFooterProps> = ({ children, className }) => {
  return (
    <div
      className={clsx(
        'px-4 py-3 border-t border-pf-border bg-pf-bg-1',
        className
      )}
    >
      {children}
    </div>
  );
};

// Attach sub-components
Card.Header = CardHeader;
Card.Body = CardBody;
Card.Footer = CardFooter;

export default Card;
