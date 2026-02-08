import { render, screen } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import { PageHeader } from '../PageHeader';

const MockIcon = ({ className }: { className?: string }) => (
  <svg data-testid="mock-icon" className={className}></svg>
);

describe('PageHeader', () => {
  it('should render title', () => {
    render(<PageHeader title="Test Page" />);

    expect(screen.getByText('Test Page')).toBeInTheDocument();
  });

  it('should render subtitle when provided', () => {
    render(
      <PageHeader 
        title="Test Page" 
        subtitle="This is a test subtitle" 
      />
    );

    expect(screen.getByText('Test Page')).toBeInTheDocument();
    expect(screen.getByText('This is a test subtitle')).toBeInTheDocument();
  });

  it('should render icon when provided', () => {
    render(
      <PageHeader 
        title="Test Page" 
        icon={MockIcon}
      />
    );

    expect(screen.getByTestId('mock-icon')).toBeInTheDocument();
  });

  it('should render actions when provided', () => {
    render(
      <PageHeader 
        title="Test Page"
        actions={<button>Action Button</button>}
      />
    );

    expect(screen.getByText('Action Button')).toBeInTheDocument();
  });

  it('should render without optional props', () => {
    render(<PageHeader title="Simple Title" />);

    const heading = screen.getByRole('heading', { level: 2 });
    expect(heading).toHaveTextContent('Simple Title');
  });

  it('should render all props together', () => {
    render(
      <PageHeader 
        title="Complete Page"
        subtitle="With all features"
        icon={MockIcon}
        actions={<button>Add New</button>}
      />
    );

    expect(screen.getByText('Complete Page')).toBeInTheDocument();
    expect(screen.getByText('With all features')).toBeInTheDocument();
    expect(screen.getByTestId('mock-icon')).toBeInTheDocument();
    expect(screen.getByText('Add New')).toBeInTheDocument();
  });

  it('should apply correct heading level', () => {
    render(<PageHeader title="Test Page" />);

    const heading = screen.getByRole('heading', { level: 2 });
    expect(heading).toBeInTheDocument();
  });

  it('should render icon with correct styling', () => {
    render(<PageHeader title="Test Page" icon={MockIcon} />);

    const icon = screen.getByTestId('mock-icon');
    expect(icon).toHaveClass('h-6', 'w-6', 'mr-2');
  });
});
