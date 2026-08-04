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
  // and `:hover` scores (0,1,1) against the selected class's (0,1,0) inside the
  // same utilities layer -- so an unconditional hover would repaint a selected
  // row and erase its highlight (#1088). Every selectable table row whose only
  // selection signal is a background is conditional for that reason. (Some
  // non-table pickers -- LocationTreePicker, WorkerSelector -- do pair an
  // unconditional hover with a tinted selection, but they also carry accent
  // text or a ring, so the selection survives the repaint.)
  const classes = `${isSelected ? 'bg-pf-bg-2' : 'hover:bg-pf-hover-overlay'} transition-colors ${className ?? ''}`.trim();

  return (
    <tr ref={ref} className={classes} {...rest}>
      {children}
    </tr>
  );
}

