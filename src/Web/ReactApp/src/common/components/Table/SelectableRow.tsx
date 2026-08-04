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
  //
  // The hover is applied only to the UNSELECTED branch. `bg-*` compiles to
  // `background-color`, which replaces the base rather than layering over it,
  // so an unconditional hover would paint a selected row the same colour as an
  // unselected one and erase the selection highlight. The three other rows that
  // use this overlay (IndexedFilesList, BulkTagAssignmentModal, FileRow) are all
  // conditional for that reason; this one is now consistent with them.
  const classes = `${isSelected ? 'bg-pf-bg-2' : 'hover:bg-pf-hover-overlay'} transition-colors ${className ?? ''}`.trim();

  return (
    <tr ref={ref} className={classes} {...rest}>
      {children}
    </tr>
  );
}

