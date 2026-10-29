import { useState } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ComponentProps, ReactElement } from 'react';
import { act, cleanup, fireEvent, render as renderTree, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { PrinterCoordinateRow } from '@/features/printers/components/PrinterCoordinateRow';
import { USER_SETTINGS_KEY } from '@/features/settings/hooks/useUserSettings';

let allowed = true;
vi.mock('@/features/auth/hooks/useAuth', () => ({ useAuth: () => ({ hasPermission: () => allowed }) }));
type Props = ComponentProps<typeof PrinterCoordinateRow>;
function Entry({ initialValues = { X: '', Y: '', Z: '' }, ...props }: Partial<Props> & { initialValues?: Props['values'] }) {
  const [values, setValues] = useState(initialValues);
  return <PrinterCoordinateRow values={values} homedAxes="xyz"
    onChange={(axis, value) => setValues(previous => ({ ...previous, [axis]: value }))}
    disabled={false} onMove={vi.fn()} {...props} />;
}
const input = (axis = 'X', absolute = true) => screen.getByRole('spinbutton', { name: `${axis} ${absolute ? 'absolute target' : 'movement amount'}` });
const enter = (value: string, axis = 'X', absolute = true) => fireEvent.change(input(axis, absolute), { target: { value } });
let accountMode: 'Guided' | 'Expert' = 'Guided';
function render(element: ReactElement) {
  const client = new QueryClient({ defaultOptions: { queries: { enabled: false, retry: false } } });
  client.setQueryData(USER_SETTINGS_KEY, { userId: 'coordinate-user', printerControlMode: accountMode, rowVersion: 'v1' });
  return { client, ...renderTree(<QueryClientProvider client={client}>{element}</QueryClientProvider>) };
}
beforeEach(() => { accountMode = 'Guided'; allowed = true; });
afterEach(cleanup);

describe('shared coordinate entry', () => {
  it('has explicit modes, no GO and no inferred targets from telemetry', () => {
    render(<Entry onMoveTo={vi.fn()} positions={{ X: 100, Y: 200, Z: 10 }} />);
    expect(screen.queryByRole('button', { name: /GO/ })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Absolute' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('button', { name: 'Relative' })).toHaveAttribute('aria-pressed', 'false');
    for (const field of screen.getAllByRole('spinbutton')) expect(field).toHaveValue(null);
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('Tabs a changed absolute target to only that axis, ignoring other drafts', async () => {
    const onMoveTo = vi.fn();
    const user = userEvent.setup();
    render(<Entry onMoveTo={onMoveTo} initialValues={{ X: '', Y: 130, Z: 15 }} />);
    await user.click(input());
    await user.type(input(), '750.5');
    await user.tab();
    expect(onMoveTo).toHaveBeenCalledExactlyOnceWith({ x: 750.5 });
  });

  it('consumes Enter synchronously, prevents repeated Enter/blur, and locks all inputs while pending', async () => {
    let resolve!: () => void;
    const onMoveTo = vi.fn(() => new Promise<void>(done => { resolve = done; }));
    render(<Entry onMoveTo={onMoveTo} />);
    fireEvent.focus(input()); enter('12.5');
    const field = input();
    fireEvent.keyDown(field, { key: 'Enter' });
    fireEvent.keyDown(field, { key: 'Enter' });
    fireEvent.blur(field);
    expect(onMoveTo).toHaveBeenCalledExactlyOnceWith({ x: 12.5 });
    for (const control of [...screen.getAllByRole('spinbutton'), ...screen.getAllByRole('button')]) expect(control).toBeDisabled();
    await act(async () => resolve());
    expect(input()).toBeEnabled();
    fireEvent.blur(input());
    expect(onMoveTo).toHaveBeenCalledTimes(1);
  });

  it('supports deliberate repeated relative deltas after completion without replay on blur', async () => {
    const onMove = vi.fn();
    render(<Entry onMove={onMove} onMoveTo={vi.fn()} />);
    fireEvent.click(screen.getByRole('button', { name: 'Relative' }));
    for (let count = 1; count <= 2; count++) {
      fireEvent.focus(input('Y', false)); enter('-2', 'Y', false);
      fireEvent.keyDown(input('Y', false), { key: 'Enter' });
      fireEvent.blur(input('Y', false));
      await waitFor(() => expect(input('Y', false)).toBeEnabled());
      expect(onMove).toHaveBeenCalledTimes(count);
      expect(onMove).toHaveBeenLastCalledWith('Y', -2);
      expect(input('Y', false)).toHaveValue(null);
    }
  });

  it('does not submit pristine, unchanged, invalid or escaped drafts', () => {
    const onMoveTo = vi.fn();
    render(<Entry onMoveTo={onMoveTo} initialValues={{ X: 20, Y: '', Z: '' }} />);
    fireEvent.focus(input()); enter('21'); enter('20'); fireEvent.blur(input());
    fireEvent.focus(input()); enter('30'); fireEvent.keyDown(input(), { key: 'Escape' }); fireEvent.blur(input());
    fireEvent.focus(input()); enter('4'); enter(''); fireEvent.keyDown(input(), { key: 'Enter' }); fireEvent.blur(input());
    fireEvent.keyDown(input('Y'), { key: 'Enter' });
    expect(onMoveTo).not.toHaveBeenCalled();
    expect(screen.getByRole('alert')).toHaveTextContent('finite number for X');
  });

  it('cancels a draft when tabbing or clicking to a mode button and never reinterprets it', async () => {
    const onMoveTo = vi.fn();
    const onMove = vi.fn();
    const user = userEvent.setup();
    render(<Entry onMoveTo={onMoveTo} onMove={onMove} />);
    await user.click(input()); await user.type(input(), '50');
    await user.click(screen.getByRole('button', { name: 'Relative' }));
    expect(input('X', false)).toHaveValue(null);
    expect(onMoveTo).not.toHaveBeenCalled();
    await user.click(input('X', false)); await user.type(input('X', false), '5');
    await user.tab({ shift: true });
    await user.keyboard(' ');
    expect(onMove).not.toHaveBeenCalled();
    expect(input()).toHaveValue(null);
  });

  it.each([undefined, '', 'unknown', 'not available', 'xy', 'x', 'yz', 'xz'])('submits coordinates without homing evidence (%s)', async homedAxes => {
    const move = vi.fn();
    render(<Entry homedAxes={homedAxes} onMoveTo={move} />);
    for (const field of screen.getAllByRole('spinbutton')) expect(field).toBeEnabled();
    enter('40'); fireEvent.blur(input());
    expect(move).toHaveBeenCalledExactlyOnceWith({ x: 40 });
    await waitFor(() => expect(input()).toBeEnabled());
    fireEvent.click(screen.getByRole('button', { name: 'Relative' }));
    for (const field of screen.getAllByRole('spinbutton')) expect(field).toBeEnabled();
  });

  it('enables all coordinates only with positive full XYZ homing, regardless of axis order or case', () => {
    render(<Entry homedAxes="ZYX" onMoveTo={vi.fn()} />);
    for (const field of screen.getAllByRole('spinbutton')) expect(field).toBeEnabled();
    expect(screen.queryByText(/Home X, Y and Z before coordinate entry/)).not.toBeInTheDocument();
  });

  it.each(['disabled', 'permission'] as const)('blocks both modes and fields when %s', gate => {
    allowed = gate !== 'permission';
    render(<Entry disabled={gate === 'disabled'} onMoveTo={vi.fn()} />);
    for (const control of [...screen.getAllByRole('spinbutton'), ...screen.getAllByRole('button')]) expect(control).toBeDisabled();
  });

  it.each(['xy', 'xz', 'yz', '', undefined, 'unknown'])('preserves deliberate edits when homing telemetry becomes %s', async homedAxes => {
    const onMoveTo = vi.fn();
    const onMove = vi.fn();
    const { rerender, client } = render(<Entry onMoveTo={onMoveTo} onMove={onMove} />);
    fireEvent.focus(input()); enter('20');
    enter('30', 'Y');
    rerender(<QueryClientProvider client={client}><Entry homedAxes={homedAxes} onMoveTo={onMoveTo} onMove={onMove} /></QueryClientProvider>);
    for (const field of screen.getAllByRole('spinbutton')) expect(field).toBeEnabled();
    expect(input()).toHaveValue(20);
    expect(input('Y')).toHaveValue(30);
    fireEvent.keyDown(input(), { key: 'Enter' });
    expect(onMoveTo).toHaveBeenCalledExactlyOnceWith({ x: 20 });
    await waitFor(() => expect(input()).toBeEnabled());
    fireEvent.click(screen.getByRole('button', { name: 'Relative' }));
    fireEvent.focus(input('X', false)); enter('5', 'X', false);
    rerender(<QueryClientProvider client={client}><Entry homedAxes={homedAxes} onMoveTo={onMoveTo} onMove={onMove} /></QueryClientProvider>);
    expect(input('X', false)).toHaveValue(5);
    fireEvent.keyDown(input('X', false), { key: 'Enter' });
    fireEvent.blur(input('X', false));
    expect(onMove).toHaveBeenCalledExactlyOnceWith('X', 5);
    await waitFor(() => expect(input('X', false)).toBeEnabled());
  });

  it('hides help in Expert but keeps validation', () => {
    accountMode = 'Expert';
    render(<Entry onMoveTo={vi.fn()} />);
    expect(screen.queryByText(/Tab or Enter sends/)).not.toBeInTheDocument();
    enter('4'); enter(''); fireEvent.keyDown(input(), { key: 'Enter' });
    expect(screen.getByRole('alert')).toBeVisible();
  });
});
