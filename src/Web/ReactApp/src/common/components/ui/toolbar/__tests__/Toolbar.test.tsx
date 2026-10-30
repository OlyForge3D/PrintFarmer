import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { Toolbar, ToolbarField, SearchField, SortControl, FilterChip, FilterButton, FiltersDisclosure, RefreshButton, Pagination } from '@/common/components/ui/toolbar';
import { ViewToggle } from '@/common/components/ui/ViewToggle';

describe('Toolbar controls', () => {
  it('uses a named group without claiming toolbar keyboard navigation', () => {
    render(<Toolbar aria-label="Job controls"><Toolbar.Group><span>Filters</span></Toolbar.Group><Toolbar.Spacer /><Toolbar.Divider /></Toolbar>);
    expect(screen.getByRole('group', { name: 'Job controls' })).toBeVisible();
    expect(screen.queryByRole('toolbar')).not.toBeInTheDocument();
  });

  it('keeps search controlled and clears through the supplied callback', () => {
    const change = vi.fn();
    const clear = vi.fn();
    render(<ToolbarField htmlFor="search" label="Search files">
      <SearchField id="search" value="gear" onChange={change} onClear={clear} label="Search files" />
    </ToolbarField>);
    fireEvent.change(screen.getByRole('searchbox', { name: 'Search files' }), { target: { value: 'bolt' } });
    expect(change).toHaveBeenCalledOnce();
    fireEvent.click(screen.getByRole('button', { name: 'Clear search' }));
    expect(clear).toHaveBeenCalledOnce();
    expect(screen.getByRole('searchbox', { name: 'Search files' })).toHaveFocus();
  });

  it('reports the current sort direction separately from its next action', () => {
    const change = vi.fn();
    const direction = vi.fn();
    const { rerender } = render(<SortControl value="name" onChange={change} direction="asc" onDirectionChange={direction}>
      <option value="name">Name</option><option value="date">Date</option>
    </SortControl>);
    fireEvent.change(screen.getByRole('combobox', { name: 'Sort by' }), { target: { value: 'date' } });
    expect(change).toHaveBeenCalledOnce();
    fireEvent.click(screen.getByRole('button', { name: 'Sort direction: ascending' }));
    expect(direction).toHaveBeenCalledOnce();
    expect(screen.getByTitle('Ascending (click for descending)')).toBeVisible();
    rerender(<SortControl direction="desc" onDirectionChange={direction}><option>Name</option></SortControl>);
    expect(screen.getByRole('button', { name: 'Sort direction: descending' })).toHaveAttribute('title', 'Descending (click for ascending)');
  });

  it('exposes filter and view state without changing handler contracts', () => {
    const onClick = vi.fn();
    const onChange = vi.fn();
    render(<>
      <FilterChip selected onClick={onClick}>Ready</FilterChip>
      <FilterButton count={2} expanded aria-controls="filters" />
      <ViewToggle value="grid" onChange={onChange} size="md" options={[
        { mode: 'grid', kind: 'cards', title: 'Grid view' }, { mode: 'table', kind: 'table', title: 'Table view' },
      ]} />
    </>);
    fireEvent.click(screen.getByRole('button', { name: 'Ready' }));
    expect(onClick).toHaveBeenCalledOnce();
    expect(screen.getByRole('button', { name: 'Ready' })).toHaveAttribute('aria-pressed', 'true');
    expect(screen.getByRole('button', { name: 'Filters (2)' })).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByRole('button', { name: 'Grid view' })).toHaveAttribute('aria-pressed', 'true');
    fireEvent.click(screen.getByRole('button', { name: 'Table view' }));
    expect(onChange).toHaveBeenCalledWith('table');
  });

  it('links the filter trigger to an expandable panel and exposes its active count', () => {
    const reset = vi.fn();
    const expandedChange = vi.fn();
    render(<Toolbar aria-label="Inventory controls">
      <FiltersDisclosure activeCount={2} onReset={reset} onExpandedChange={expandedChange}>
        <label htmlFor="material">Material</label>
        <select id="material"><option>PLA</option></select>
      </FiltersDisclosure>
      <SearchField value="" onChange={vi.fn()} onClear={vi.fn()} />
    </Toolbar>);

    const trigger = screen.getByRole('button', { name: 'Filters (2)' });
    const panelId = trigger.getAttribute('aria-controls');
    expect(panelId).toBeTruthy();
    expect(document.getElementById(panelId!)).toHaveAttribute('hidden');
    expect(trigger).toHaveAttribute('aria-expanded', 'false');

    fireEvent.click(trigger);
    const panel = screen.getByRole('region', { name: 'Filters' });
    expect(panel).toHaveAttribute('id', panelId);
    expect(trigger).toHaveAttribute('aria-expanded', 'true');
    expect(expandedChange).toHaveBeenLastCalledWith(true);
    expect(screen.getByLabelText('Material')).toBeVisible();
    fireEvent.click(screen.getByRole('button', { name: 'Reset filters' }));
    expect(reset).toHaveBeenCalledOnce();
    expect(trigger).toHaveFocus();
    fireEvent.click(trigger);
    expect(expandedChange).toHaveBeenLastCalledWith(false);
  });

  it('disables Reset filters when no filters are active', () => {
    render(<Toolbar aria-label="Inventory controls">
      <FiltersDisclosure activeCount={0} onReset={vi.fn()}>
        <span>Material</span>
      </FiltersDisclosure>
    </Toolbar>);

    expect(screen.getByRole('button', { name: 'Filters' })).toBeVisible();
    fireEvent.click(screen.getByRole('button', { name: 'Filters' }));
    expect(screen.getByRole('button', { name: 'Reset filters' })).toBeDisabled();
  });

  it('retains the refresh name while busy and prevents activation', () => {
    const refresh = vi.fn();
    render(<RefreshButton busy onClick={refresh} />);
    const button = screen.getByRole('button', { name: 'Refresh' });
    expect(button).toBeDisabled();
    expect(button).toHaveAttribute('aria-busy', 'true');
    fireEvent.click(button);
    expect(refresh).not.toHaveBeenCalled();
  });

  it('preserves pagination bounds and page size callbacks', () => {
    const page = vi.fn();
    const size = vi.fn();
    render(<Pagination page={1} totalPages={3} onPageChange={page} pageSize={25} onPageSizeChange={size} />);
    expect(screen.getByRole('button', { name: 'Previous' })).toBeDisabled();
    expect(screen.getByText('Page 1 of 3')).toBeVisible();
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));
    expect(page).toHaveBeenCalledWith(2);
    expect(screen.getByRole('button', { name: 'Next' })).toHaveAttribute('type', 'button');
    fireEvent.change(screen.getByRole('combobox', { name: 'Page size' }), { target: { value: '50' } });
    expect(size).toHaveBeenCalledWith(50);
  });

  it('disables pagination and sort actions without submitting a containing form', () => {
    const page = vi.fn();
    const sort = vi.fn();
    const { rerender } = render(<form>
      <Pagination page={3} totalPages={3} onPageChange={page} disabled />
      <SortControl direction="asc" onDirectionChange={sort} disabled><option>Name</option></SortControl>
    </form>);
    for (const button of screen.getAllByRole('button')) {
      expect(button).toBeDisabled();
      expect(button).toHaveAttribute('type', 'button');
      fireEvent.click(button);
    }
    expect(page).not.toHaveBeenCalled();
    expect(sort).not.toHaveBeenCalled();
    rerender(<Pagination page={3} totalPages={3} onPageChange={page} />);
    expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled();
    fireEvent.click(screen.getByRole('button', { name: 'Previous' }));
    expect(page).toHaveBeenCalledWith(2);
  });
});
