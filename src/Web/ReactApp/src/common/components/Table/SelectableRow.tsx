import React from 'react';

interface SelectableRowProps extends React.HTMLAttributes<HTMLTableRowElement> {
  isSelected?: boolean;
  ref?: React.Ref<HTMLTableRowElement>;
}

export function SelectableRow({ isSelected = false, className, children, ref, ...rest }: SelectableRowProps) {
  const classes = `${isSelected ? 'bg-pf-bg-2' : ''} hover:bg-pf-bg-secondary transition-colors ${className ?? ''}`.trim();

  return (
    <tr ref={ref} className={classes} {...rest}>
      {children}
    </tr>
  );
}

