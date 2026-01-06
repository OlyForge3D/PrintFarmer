import React from 'react';

interface SelectableRowProps extends React.HTMLAttributes<HTMLTableRowElement> {
  isSelected?: boolean;
}

export function SelectableRow({ isSelected = false, className, children, ...rest }: SelectableRowProps) {
  const classes = `${isSelected ? 'bg-pf-bg-2' : ''} hover:bg-pf-bg-secondary transition-colors ${className ?? ''}`.trim();

  return (
    <tr className={classes} {...rest}>
      {children}
    </tr>
  );
}

