import React from 'react';

interface SelectableRowProps extends React.HTMLAttributes<HTMLTableRowElement> {
  isSelected?: boolean;
  ref?: React.Ref<HTMLTableRowElement>;
}

export function SelectableRow({ isSelected = false, className, children, ref, ...rest }: SelectableRowProps) {
  // Hover uses the translucent overlay (DESIGN-LANGUAGE "Tables → Hover row")
  // rather than a surface token. This component is rendered into containers of
  // differing depth -- PrinterTableView wraps it in bg-1, CameraManagementPanel
  // in bg-0 -- so any fixed surface token is inert in at least one consumer.
  // The overlay composites over whatever is behind it, and over the selected
  // bg-2 as well, so hovering a selected row no longer erases its highlight.
  const classes = `${isSelected ? 'bg-pf-bg-2' : ''} hover:bg-[var(--pf-hover-overlay)] transition-colors ${className ?? ''}`.trim();

  return (
    <tr ref={ref} className={classes} {...rest}>
      {children}
    </tr>
  );
}

