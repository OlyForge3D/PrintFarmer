import { useState } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ComponentProps, ReactElement } from 'react';
import { act, cleanup, fireEvent, render as renderTree, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { PrinterCoordinateRow } from '@/features/printers/components/PrinterCoordinateRow';
import { USER_SETTINGS_KEY } from '@/features/settings/hooks/useUserSettings';

type Props = ComponentProps<typeof PrinterCoordinateRow>;
function Entry({ initialValues = { X: '', Y: '', Z: '' }, ...props }: Partial<Props> & { initialValues?: Props['values'] }) {
  const [values, setValues] = useState(initialValues);
  return <PrinterCoordinateRow values={values} onChange={(axis, value) => setValues(previous => ({ ...previous, [axis]: value }))} disabled={false} onMove={vi.fn()} {...props} />;
}
const enter = (axis: string, value: string, absolute = true) => fireEvent.change(screen.getByRole('spinbutton', { name: `${axis} ${absolute ? 'absolute target' : 'movement amount'}` }), { target: { value } });
let accountMode: 'Guided' | 'Expert' = 'Guided';
function render(element: ReactElement) {
  const client = new QueryClient({ defaultOptions: { queries: { enabled: false, retry: false } } });
  client.setQueryData(USER_SETTINGS_KEY, { userId: 'coordinate-user', printerControlMode: accountMode, rowVersion: 'v1' });
  return renderTree(<QueryClientProvider client={client}>{element}</QueryClientProvider>);
}
beforeEach(() => { accountMode = 'Guided'; });
afterEach(cleanup);

describe('shared coordinate row', () => {
  it('has no pristine errors and does not infer missing targets from telemetry', () => {
    const onMoveTo = vi.fn();
    render(<Entry onMoveTo={onMoveTo} positions={{ X: 100, Y: 200, Z: 10 }} />);
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.getByText('Enter X, Y, and Z targets in mm to enable GO.')).toBeVisible();
    expect(screen.getByRole('button', { name: 'GO to absolute position' })).toBeDisabled();
    for (const input of screen.getAllByRole('spinbutton')) {
      expect(input).toHaveValue(null);
      expect(input).not.toHaveAttribute('aria-invalid');
      expect(input).not.toHaveAttribute('max');
    }
    fireEvent.keyDown(screen.getByLabelText('X absolute target'), { key: 'Enter' });
    expect(screen.getByRole('alert')).toHaveTextContent('Enter a finite number for X, Y, Z');
    expect(onMoveTo).not.toHaveBeenCalled();
  });

  it('validates on blur, associates the error, and clears it after correction', () => {
    render(<Entry onMoveTo={vi.fn()} />);
    const input = screen.getByLabelText('X absolute target');
    fireEvent.focus(input);
    fireEvent.blur(input);
    expect(input).toHaveAttribute('aria-invalid', 'true');
    expect(input).toHaveAttribute('aria-describedby', screen.getByRole('alert').id);
    enter('X', '1');
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(input).not.toHaveAttribute('aria-invalid');
  });

  it('requires all explicit finite axes, allows coordinates above 500, and submits one absolute move', async () => {
    const onMoveTo = vi.fn();
    const onMove = vi.fn();
    render(<Entry onMoveTo={onMoveTo} onMove={onMove} />);
    enter('X', '750'); enter('Y', '-10.5');
    const go = screen.getByRole('button', { name: 'GO to absolute position' });
    expect(go).toBeDisabled();
    enter('Z', '0');
    expect(go).toBeEnabled();
    fireEvent.click(go);
    expect(onMoveTo).toHaveBeenCalledExactlyOnceWith({ x: 750, y: -10.5, z: 0 });
    expect(onMove).not.toHaveBeenCalled();
    await waitFor(() => expect(go).not.toHaveAttribute('aria-busy', 'true'));
  });

  it.each([Infinity, -Infinity, NaN, 'Infinity', ' '])('rejects a non-finite/missing coordinate %s', value => {
    render(<Entry initialValues={{ X: value, Y: 1, Z: 2 }} onMoveTo={vi.fn()} />);
    expect(screen.getByRole('button', { name: 'GO to absolute position' })).toBeDisabled();
  });

  it('preserves relative per-axis Enter and sparse partial GO', async () => {
    const onMove = vi.fn();
    render(<Entry onMove={onMove} perAxisEnter />);
    enter('X', '15', false); enter('Y', '-2', false);
    fireEvent.keyDown(screen.getByLabelText('Y movement amount'), { key: 'Enter' });
    expect(onMove).toHaveBeenCalledExactlyOnceWith('Y', -2);
    await waitFor(() => expect(screen.getByRole('button', { name: 'GO by movement amounts' })).toBeEnabled());
    onMove.mockClear();
    fireEvent.click(screen.getByRole('button', { name: 'GO by movement amounts' }));
    await waitFor(() => expect(onMove).toHaveBeenCalledTimes(2));
    expect(onMove).toHaveBeenNthCalledWith(1, 'X', 15);
    expect(onMove).toHaveBeenNthCalledWith(2, 'Y', -2);
  });

  it('shows GO pending immediately for coordinate Enter, without relying on the printer lock', async () => {
    let resolve!: () => void;
    const onMoveTo = vi.fn(() => new Promise<void>(done => { resolve = done; }));
    render(<Entry initialValues={{ X: 1, Y: 2, Z: 3 }} onMoveTo={onMoveTo} />);
    fireEvent.keyDown(screen.getByLabelText('X absolute target'), { key: 'Enter' });
    const go = screen.getByRole('button', { name: 'GO to absolute position' });
    expect(go).toHaveAttribute('aria-busy', 'true');
    expect(go).toBeDisabled();
    for (const input of screen.getAllByRole('spinbutton')) expect(input).toBeDisabled();
    await act(async () => resolve());
    expect(go).toHaveAttribute('aria-busy', 'false');
  });

  it('keeps actual errors in Expert while hiding expected-input hints', () => {
    accountMode = 'Expert';
    render(<Entry onMoveTo={vi.fn()} />);
    expect(screen.queryByText(/targets in mm to enable GO/)).not.toBeInTheDocument();
    fireEvent.keyDown(screen.getByLabelText('X absolute target'), { key: 'Enter' });
    expect(screen.getByRole('alert')).toBeVisible();
  });

  it('uses container reflow with flexible axes and a nonshrinking compact GO track', () => {
    const { container } = render(<Entry />);
    const row = container.querySelector('[data-printer-coordinate-row]')!;
    expect(row).toHaveClass('@container');
    expect(row.firstElementChild).toHaveClass('@[19rem]:grid-cols-[repeat(3,minmax(0,1fr))_2.75rem]');
    const go = screen.getByRole('button', { name: 'GO by movement amounts' });
    expect(go).toHaveClass('w-11!', 'h-8!');
    expect(go.parentElement).toHaveClass('w-11', 'shrink-0');
    for (const input of screen.getAllByRole('spinbutton')) expect(input).toHaveClass('min-w-0');
  });
});
