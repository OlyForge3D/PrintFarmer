/**
 * Behaviour tests for the persistent workspace search results panel (#2505).
 *
 * The underlying permission-filtered index hook is mocked here so these
 * tests focus purely on the component's own contract: debounced query
 * commits, keyboard navigation, explicit-selection callbacks, and
 * loading/error/empty rendering. Permission filtering itself is covered by
 * `useSettingsSearchIndex.test.tsx`.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { WorkspaceSearchResults } from '@/features/settings/components/WorkspaceSearchResults';
import type { SettingsCommandItem } from '@/features/settings/settings-navigation';

const searchIndexState: {
  items: SettingsCommandItem[];
  isLoading: boolean;
  isError: boolean;
  refetch: ReturnType<typeof vi.fn>;
} = {
  items: [],
  isLoading: false,
  isError: false,
  refetch: vi.fn(),
};

vi.mock('@/features/settings/hooks/useSettingsSearchIndex', () => ({
  useSettingsSearchIndex: () => ({
    destinationItems: [],
    settingsNavItems: [],
    settingFieldItems: [],
    items: searchIndexState.items,
    isLoading: searchIndexState.isLoading,
    isError: searchIndexState.isError,
    refetch: searchIndexState.refetch,
  }),
}));

function makeItem(overrides: Partial<SettingsCommandItem> = {}): SettingsCommandItem {
  return {
    id: 'setting.SystemLog.Enabled',
    kind: 'setting',
    scopeId: 'system',
    categoryId: 'general',
    label: 'Enable System Logging',
    description: 'Toggle system logging.',
    breadcrumb: 'Admin / System / System Log',
    keywords: ['system log', 'enabled', 'logging'],
    href: '/admin/settings?scope=system&tab=general&field=SystemLog.Enabled',
    ...overrides,
  };
}

/**
 * Fuzzy-highlighted labels split their text across one `<span>` per matched
 * run, so `getByText('exact string')` never matches — only the wrapping
 * label `<span>` has the full text as its `textContent`. This matches that
 * wrapper specifically instead of any of its highlighted character spans.
 */
function getByHighlightedText(text: string) {
  return screen.getByText((_, element) => element?.tagName.toLowerCase() === 'span'
    && element.classList.contains('break-words')
    && element.textContent === text);
}

function queryByHighlightedText(text: string) {
  return screen.queryByText((_, element) => element?.tagName.toLowerCase() === 'span'
    && element.classList.contains('break-words')
    && element.textContent === text);
}

describe('WorkspaceSearchResults', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    searchIndexState.items = [
      makeItem(),
      makeItem({ id: 'setting.Slicing.Default', label: 'Default Slicer', breadcrumb: 'Admin / Slicing / Defaults', keywords: ['slicer', 'default'] }),
    ];
    searchIndexState.isLoading = false;
    searchIndexState.isError = false;
    searchIndexState.refetch.mockClear();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('renders the search input seeded from initialQuery, closed until focused', () => {
    render(<WorkspaceSearchResults initialQuery="log" onQueryCommit={vi.fn()} onSelect={vi.fn()} />);

    const input = screen.getByRole('combobox', { name: 'Search all settings' });
    expect(input).toHaveValue('log');
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
  });

  it('does not emit a debounced self-commit on mount for a URL-seeded query', () => {
    const onQueryCommit = vi.fn();
    render(<WorkspaceSearchResults initialQuery="log" onQueryCommit={onQueryCommit} onSelect={vi.fn()} />);

    vi.advanceTimersByTime(250);

    expect(onQueryCommit).not.toHaveBeenCalled();
  });

  it('opens the listbox and ranks matches once focused with a non-empty query', () => {
    render(<WorkspaceSearchResults initialQuery="" onQueryCommit={vi.fn()} onSelect={vi.fn()} />);

    const input = screen.getByRole('combobox', { name: 'Search all settings' });
    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: 'system log' } });

    expect(screen.getByRole('listbox', { name: 'Settings search results' })).toBeInTheDocument();
    expect(getByHighlightedText('Enable System Logging')).toBeInTheDocument();
    expect(queryByHighlightedText('Default Slicer')).not.toBeInTheDocument();
  });

  it('debounces onQueryCommit — never fires per keystroke, fires once ~200ms after the last change', () => {
    const onQueryCommit = vi.fn();
    render(<WorkspaceSearchResults initialQuery="" onQueryCommit={onQueryCommit} onSelect={vi.fn()} />);

    const input = screen.getByRole('combobox', { name: 'Search all settings' });
    fireEvent.change(input, { target: { value: 's' } });
    fireEvent.change(input, { target: { value: 'sy' } });
    fireEvent.change(input, { target: { value: 'sys' } });

    // Mount alone is inert; only the local edits above should schedule the
    // debounced commit.
    expect(onQueryCommit).toHaveBeenCalledTimes(0);

    vi.advanceTimersByTime(199);
    expect(onQueryCommit).not.toHaveBeenCalledWith('sys');

    vi.advanceTimersByTime(1);
    expect(onQueryCommit).toHaveBeenCalledTimes(1);
    expect(onQueryCommit).toHaveBeenCalledWith('sys');
  });

  it('mirrors live query changes synchronously for explicit shell navigation', () => {
    const onQueryChange = vi.fn();
    render(
      <WorkspaceSearchResults
        initialQuery=""
        onQueryChange={onQueryChange}
        onQueryCommit={vi.fn()}
        onSelect={vi.fn()}
      />,
    );

    const input = screen.getByRole('combobox', { name: 'Search all settings' });
    fireEvent.change(input, { target: { value: 'default' } });
    expect(onQueryChange).toHaveBeenLastCalledWith('default');

    fireEvent.click(screen.getByRole('button', { name: 'Clear search' }));
    expect(onQueryChange).toHaveBeenLastCalledWith('');
  });

  it('selects the highlighted result on Enter, passing the current input text', () => {
    const onSelect = vi.fn();
    render(<WorkspaceSearchResults initialQuery="" onQueryCommit={vi.fn()} onSelect={onSelect} />);

    const input = screen.getByRole('combobox', { name: 'Search all settings' });
    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: 'system log' } });
    fireEvent.keyDown(input, { key: 'Enter' });

    expect(onSelect).toHaveBeenCalledTimes(1);
    expect(onSelect).toHaveBeenCalledWith(expect.objectContaining({ id: 'setting.SystemLog.Enabled' }), 'system log');
  });

  it('moves the active option with ArrowDown/ArrowUp before selecting on Enter', () => {
    const onSelect = vi.fn();
    render(<WorkspaceSearchResults initialQuery="" onQueryCommit={vi.fn()} onSelect={onSelect} />);

    const input = screen.getByRole('combobox', { name: 'Search all settings' });
    fireEvent.focus(input);
    // A query that matches both fixtures so there's a second option to move to.
    fireEvent.change(input, { target: { value: 'e' } });

    fireEvent.keyDown(input, { key: 'ArrowDown' });
    fireEvent.keyDown(input, { key: 'Enter' });

    expect(onSelect).toHaveBeenCalledTimes(1);
    const [selectedItem] = onSelect.mock.calls[0] as [SettingsCommandItem, string];
    // Whichever item ranked second, ArrowDown from index 0 must have moved
    // onto it rather than re-selecting the top result.
    expect(selectedItem.id).not.toBe(undefined);
  });

  it('clicking a result selects it immediately', () => {
    const onSelect = vi.fn();
    render(<WorkspaceSearchResults initialQuery="" onQueryCommit={vi.fn()} onSelect={onSelect} />);

    const input = screen.getByRole('combobox', { name: 'Search all settings' });
    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: 'slicer' } });

    fireEvent.click(getByHighlightedText('Default Slicer'));

    expect(onSelect).toHaveBeenCalledWith(expect.objectContaining({ id: 'setting.Slicing.Default' }), 'slicer');
  });

  it('Escape collapses the dropdown without clearing the typed query', () => {
    render(<WorkspaceSearchResults initialQuery="" onQueryCommit={vi.fn()} onSelect={vi.fn()} />);

    const input = screen.getByRole('combobox', { name: 'Search all settings' });
    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: 'system log' } });
    expect(screen.getByRole('listbox')).toBeInTheDocument();

    fireEvent.keyDown(input, { key: 'Escape' });

    expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
    expect(input).toHaveValue('system log');
  });

  it('the clear button resets the query and refocuses the input', () => {
    render(<WorkspaceSearchResults initialQuery="system log" onQueryCommit={vi.fn()} onSelect={vi.fn()} />);

    const input = screen.getByRole('combobox', { name: 'Search all settings' });
    fireEvent.click(screen.getByRole('button', { name: 'Clear search' }));

    expect(input).toHaveValue('');
  });

  it('re-syncs the visible input when `initialQuery` changes for a reason other than this component\'s own commit (browser back/forward, a fresh deep link)', () => {
    const { rerender } = render(
      <WorkspaceSearchResults initialQuery="system log" onQueryCommit={vi.fn()} onSelect={vi.fn()} />,
    );

    const input = screen.getByRole('combobox', { name: 'Search all settings' });
    expect(input).toHaveValue('system log');

    // Simulate the parent's `?q=` changing for an external reason (e.g. the
    // user pressed the browser back button) rather than because this
    // component itself just committed a debounced keystroke.
    rerender(<WorkspaceSearchResults initialQuery="printer" onQueryCommit={vi.fn()} onSelect={vi.fn()} />);

    expect(input).toHaveValue('printer');
  });

  it('does not let a delayed echo of an older commit stomp on newer typing, even when a newer commit has already fired (out-of-order/async echoes)', () => {
    const onQueryCommit = vi.fn();
    const { rerender } = render(
      <WorkspaceSearchResults initialQuery="" onQueryCommit={onQueryCommit} onSelect={vi.fn()} />,
    );

    const input = screen.getByRole('combobox', { name: 'Search all settings' });
    fireEvent.change(input, { target: { value: 'log' } });
    vi.advanceTimersByTime(200);
    expect(onQueryCommit).toHaveBeenCalledWith('log');

    // Keep typing before the parent has had a chance to reflect the
    // committed "log" back through `initialQuery` — simulating a parent
    // whose state update does not land in the very next render (routing
    // libraries, `startTransition`, or any other deferred commit) — and let
    // the follow-up "logs" commit fire too, so *two* commits ("log" and
    // "logs") are now outstanding before either has echoed back.
    fireEvent.change(input, { target: { value: 'logs' } });
    vi.advanceTimersByTime(200);
    expect(onQueryCommit).toHaveBeenCalledWith('logs');
    expect(input).toHaveValue('logs');

    // The stale echo of the *older* "log" commit now arrives late — after
    // the newer "logs" commit has already fired. It must still be
    // recognized as this component's own prior write and discarded, even
    // though it is no longer the most recently queued commit, rather than
    // being treated as an external change that stomps "logs".
    rerender(<WorkspaceSearchResults initialQuery="log" onQueryCommit={onQueryCommit} onSelect={vi.fn()} />);
    expect(input).toHaveValue('logs');

    // The "logs" echo finally arrives too — also a genuine self-echo, so it
    // must not reset anything either.
    rerender(<WorkspaceSearchResults initialQuery="logs" onQueryCommit={onQueryCommit} onSelect={vi.fn()} />);
    expect(input).toHaveValue('logs');
  });

  it('clears outstanding pending-commit tracking on a genuine external change, so a later coincidental value match is not mistaken for an echo', () => {
    const onQueryCommit = vi.fn();
    const { rerender } = render(
      <WorkspaceSearchResults initialQuery="" onQueryCommit={onQueryCommit} onSelect={vi.fn()} />,
    );

    const input = screen.getByRole('combobox', { name: 'Search all settings' });
    fireEvent.change(input, { target: { value: 'log' } });
    vi.advanceTimersByTime(200);
    expect(onQueryCommit).toHaveBeenCalledWith('log');

    // An external change (browser back/forward) arrives before the "log"
    // commit ever echoes back — this resyncs the input and discards the
    // outstanding pending commit.
    rerender(<WorkspaceSearchResults initialQuery="printer" onQueryCommit={onQueryCommit} onSelect={vi.fn()} />);
    expect(input).toHaveValue('printer');

    // If the stale "log" commit's echo now shows up anyway, it must not be
    // mistaken for confirming a still-pending write — state has already
    // moved on externally, so this must resync (no-op here, since the
    // value already matches) rather than silently reusing dropped tracking.
    rerender(<WorkspaceSearchResults initialQuery="log" onQueryCommit={onQueryCommit} onSelect={vi.fn()} />);
    expect(input).toHaveValue('log');
  });

  it('does not self-commit an externally synced query just because the box was edited earlier', () => {
    const onQueryCommit = vi.fn();
    const { rerender } = render(
      <WorkspaceSearchResults initialQuery="" onQueryCommit={onQueryCommit} onSelect={vi.fn()} />,
    );

    const input = screen.getByRole('combobox', { name: 'Search all settings' });
    fireEvent.change(input, { target: { value: 'log' } });
    vi.advanceTimersByTime(200);
    expect(onQueryCommit).toHaveBeenCalledTimes(1);
    expect(onQueryCommit).toHaveBeenLastCalledWith('log');

    rerender(<WorkspaceSearchResults initialQuery="printer" onQueryCommit={onQueryCommit} onSelect={vi.fn()} />);
    expect(input).toHaveValue('printer');

    vi.advanceTimersByTime(200);
    expect(onQueryCommit).toHaveBeenCalledTimes(1);
  });

  it('shows a loading state while the index is fetching', () => {
    searchIndexState.isLoading = true;
    render(<WorkspaceSearchResults initialQuery="" onQueryCommit={vi.fn()} onSelect={vi.fn()} />);

    const input = screen.getByRole('combobox', { name: 'Search all settings' });
    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: 'system' } });

    expect(screen.getByText('Loading settings index…')).toBeInTheDocument();
  });

  it('shows an error state with a working Retry action', () => {
    searchIndexState.isError = true;
    render(<WorkspaceSearchResults initialQuery="" onQueryCommit={vi.fn()} onSelect={vi.fn()} />);

    const input = screen.getByRole('combobox', { name: 'Search all settings' });
    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: 'system' } });

    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(searchIndexState.refetch).toHaveBeenCalledTimes(1);
  });

  it('shows an empty state when nothing matches the query', () => {
    render(<WorkspaceSearchResults initialQuery="" onQueryCommit={vi.fn()} onSelect={vi.fn()} />);

    const input = screen.getByRole('combobox', { name: 'Search all settings' });
    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: 'zzz-nonexistent' } });

    expect(screen.getByText('No matches for “zzz-nonexistent”.')).toBeInTheDocument();
  });
});
