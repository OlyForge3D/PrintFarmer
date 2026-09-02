import '@testing-library/jest-dom';
import React from 'react';
import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import type { LocationTreeNode } from '@/types/api';
import { LocationHierarchyPanel } from '../LocationHierarchyPanel';

// Regression coverage for #2366: an empty tree with a blank search must not be
// reported as "No locations match that search." — that copy implies an active
// filter hid results, which is misleading when there is simply nothing configured.
const mockTree: LocationTreeNode[] = [
  {
    id: 'loc-1',
    name: 'Warehouse A',
    description: 'Main warehouse',
    parentId: null,
    path: '/Warehouse A',
    depth: 0,
    sortOrder: 0,
    printerCount: 2,
    totalPrinterCount: 2,
    children: [],
  },
];

describe('LocationHierarchyPanel', () => {
  it('shows an unfiltered empty state when no locations exist and the search is blank', () => {
    render(
      <LocationHierarchyPanel tree={[]} selectedId={null} onSelect={vi.fn()} />,
    );

    expect(screen.getByText('No locations configured yet.')).toBeInTheDocument();
    expect(screen.queryByText('No locations match that search.')).not.toBeInTheDocument();
  });

  it('offers a create action in the empty state when the caller allows it', () => {
    const onCreateLocation = vi.fn();
    render(
      <LocationHierarchyPanel
        tree={[]}
        selectedId={null}
        onSelect={vi.fn()}
        canCreateLocation
        onCreateLocation={onCreateLocation}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /Add location/i }));
    expect(onCreateLocation).toHaveBeenCalledTimes(1);
  });

  it('does not show the create action when the caller has no permission', () => {
    render(
      <LocationHierarchyPanel tree={[]} selectedId={null} onSelect={vi.fn()} canCreateLocation={false} />,
    );

    expect(screen.queryByRole('button', { name: /Add location/i })).not.toBeInTheDocument();
  });

  it('shows the "no matches" message when locations exist but the search filters them all out', () => {
    render(
      <LocationHierarchyPanel tree={mockTree} selectedId={null} onSelect={vi.fn()} />,
    );

    fireEvent.change(screen.getByLabelText('Search locations'), { target: { value: 'nonexistent' } });

    expect(screen.getByText('No locations match that search.')).toBeInTheDocument();
    expect(screen.queryByText('No locations configured yet.')).not.toBeInTheDocument();
  });

  it('renders the location list when locations exist and the search is blank', () => {
    render(
      <LocationHierarchyPanel tree={mockTree} selectedId={null} onSelect={vi.fn()} />,
    );

    expect(screen.getByText('Warehouse A')).toBeInTheDocument();
    expect(screen.queryByText('No locations match that search.')).not.toBeInTheDocument();
    expect(screen.queryByText('No locations configured yet.')).not.toBeInTheDocument();
  });
});
