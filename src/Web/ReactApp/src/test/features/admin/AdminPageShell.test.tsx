import { describe, it, expect } from 'vitest';
import type { ReactElement } from 'react';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { AdminPageShell } from '@/features/admin/components/AdminPageShell';

function renderShell(ui: ReactElement) {
  return render(<MemoryRouter>{ui}</MemoryRouter>);
}

describe('AdminPageShell', () => {
  it('renders a single h1 and a back link to the Control Center by default', () => {
    renderShell(
      <AdminPageShell title="Users" subtitle="Accounts and roles">
        <p>content</p>
      </AdminPageShell>,
    );

    expect(screen.getAllByRole('heading', { level: 1 })).toHaveLength(1);
    expect(screen.getByRole('heading', { level: 1, name: 'Users' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Admin Control Center' })).toHaveAttribute(
      'href',
      '/admin',
    );
  });

  it('accepts a different parent surface', () => {
    renderShell(
      <AdminPageShell title="Login audit" parent={{ label: 'Users', to: '/admin/users' }}>
        <p>content</p>
      </AdminPageShell>,
    );

    expect(screen.getByRole('link', { name: 'Users' })).toHaveAttribute('href', '/admin/users');
  });

  it('renders no back link for the hub itself', () => {
    renderShell(
      <AdminPageShell title="Admin Control Center" parent={null}>
        <p>content</p>
      </AdminPageShell>,
    );

    expect(screen.queryByRole('link')).not.toBeInTheDocument();
  });

  it('renders page actions once, in the header', () => {
    renderShell(
      <AdminPageShell title="Users" actions={<button type="button">Add user</button>}>
        <p>content</p>
      </AdminPageShell>,
    );

    expect(screen.getAllByRole('button', { name: 'Add user' })).toHaveLength(1);
  });

  it('emits content only when embedded', () => {
    renderShell(
      <AdminPageShell title="Users" subtitle="Should not appear" embedded>
        <p>content</p>
      </AdminPageShell>,
    );

    expect(screen.queryByRole('heading', { level: 1 })).not.toBeInTheDocument();
    expect(screen.queryByText('Should not appear')).not.toBeInTheDocument();
    expect(screen.queryByRole('link')).not.toBeInTheDocument();
    expect(screen.getByText('content')).toBeInTheDocument();
  });

  it('keeps the Control Center band rhythm around content', () => {
    const { container } = renderShell(
      <AdminPageShell title="Users">
        <section>one</section>
        <section>two</section>
      </AdminPageShell>,
    );

    // Bands are stacked with the same gap the hub uses, so a destination page
    // reads as the same surface as the hub it was reached from.
    expect(container.querySelector('.flex.flex-col.gap-8')).not.toBeNull();
  });
});
