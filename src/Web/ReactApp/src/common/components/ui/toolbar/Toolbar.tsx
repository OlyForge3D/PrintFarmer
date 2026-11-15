import type { HTMLAttributes, ReactNode } from 'react';
import clsx from 'clsx';

interface ToolbarProps extends HTMLAttributes<HTMLDivElement> {
  'aria-label': string;
  labelled?: boolean;
}

function Group({ segmented = false, className, ...props }: HTMLAttributes<HTMLDivElement> & { segmented?: boolean }) {
  return <div className={clsx('pf-toolbar__group', segmented && 'pf-toolbar__group--segmented', className)} {...props} />;
}

function Spacer() {
  return <div className="pf-toolbar__spacer" aria-hidden="true" />;
}

function Divider() {
  return <div className="pf-toolbar__divider" aria-hidden="true" />;
}

function Controls({ className, onFocusCapture, ...props }: HTMLAttributes<HTMLDivElement>) {
  return <div className={clsx('pf-toolbar__controls', className)} {...props} onFocusCapture={event => {
    onFocusCapture?.(event);
    const row = event.currentTarget;
    if (!row.parentElement?.classList.contains('pf-toolbar--files') || row.scrollWidth <= row.clientWidth) return;
    const bounds = row.getBoundingClientRect();
    const target = event.target.getBoundingClientRect();
    const focusInset = parseFloat(getComputedStyle(row).paddingInlineStart);
    const left = bounds.left + focusInset;
    const right = bounds.right - focusInset;
    const offset = target.left < left ? target.left - left : target.right > right ? target.right - right : 0;
    if (offset !== 0) row.scrollBy({ left: offset, behavior: 'instant' });
  }} />;
}

export function Toolbar({ labelled, className, ...props }: ToolbarProps) {
  return <div role="group" className={clsx('pf-toolbar', labelled && 'pf-toolbar--labelled', className)} {...props} />;
}
Toolbar.Group = Group;
Toolbar.Spacer = Spacer;
Toolbar.Divider = Divider;
Toolbar.Controls = Controls;

export function HeaderActions({ className, ...props }: HTMLAttributes<HTMLDivElement>) {
  return <div className={clsx('pf-header-actions', className)} {...props} />;
}

export function ToolbarField({ label, htmlFor, children, className }: {
  label: string; htmlFor: string; children: ReactNode; className?: string;
}) {
  return <div className={clsx('pf-toolbar-field', className)}>
    <label className="pf-toolbar-field__label" htmlFor={htmlFor}>{label}</label>
    {children}
  </div>;
}
