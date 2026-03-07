import { render, screen } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import { LocationManagementAdminPage } from '@/features/admin/pages/LocationManagementAdminPage';

vi.mock('@/features/locations/components/LocationManagement', () => ({
  LocationManagement: () => <div data-testid="location-management">LocationManagement Content</div>,
}));

vi.mock('@/common/components/PageTemplate', () => ({
  PageTemplate: ({ title, subtitle, children }: {
    title: string;
    subtitle?: string;
    children: React.ReactNode;
  }) => (
    <div data-testid="page-template">
      <h1>{title}</h1>
      {subtitle && <p>{subtitle}</p>}
      {children}
    </div>
  ),
}));

describe('LocationManagementAdminPage', () => {
  it('renders with correct page title', () => {
    render(<LocationManagementAdminPage />);

    expect(screen.getByText('Location Management')).toBeInTheDocument();
  });

  it('renders with correct subtitle', () => {
    render(<LocationManagementAdminPage />);

    expect(screen.getByText('Create, edit, and organize printer locations')).toBeInTheDocument();
  });

  it('renders LocationManagement component inside PageTemplate', () => {
    render(<LocationManagementAdminPage />);

    expect(screen.getByTestId('page-template')).toBeInTheDocument();
    expect(screen.getByTestId('location-management')).toBeInTheDocument();
  });
});
