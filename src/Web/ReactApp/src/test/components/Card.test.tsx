import '@testing-library/jest-dom';
import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { Card } from '@/common/components/ui/Card';

describe('Card', () => {
  describe('Basic Rendering', () => {
    it('should render children', () => {
      render(
        <Card>
          <p>Card content</p>
        </Card>
      );

      expect(screen.getByText('Card content')).toBeInTheDocument();
    });

    it('should apply custom className', () => {
      render(
        <Card className="custom-class">
          <p>Content</p>
        </Card>
      );

      const card = screen.getByText('Content').parentElement;
      expect(card).toHaveClass('custom-class');
    });
  });

  describe('Hoverable', () => {
    it('should apply hover styles when hoverable is true', () => {
      render(
        <Card hoverable>
          <p>Hoverable card</p>
        </Card>
      );

      const card = screen.getByText('Hoverable card').parentElement;
      expect(card).toHaveClass('hover:border-pf-accent/50');
      expect(card).toHaveClass('hover:shadow-md');
    });

    it('should not apply hover styles by default', () => {
      render(
        <Card>
          <p>Non-hoverable card</p>
        </Card>
      );

      const card = screen.getByText('Non-hoverable card').parentElement;
      expect(card).not.toHaveClass('hover:border-pf-accent/50');
    });
  });

  describe('Click Handler', () => {
    it('should call onClick when clicked', () => {
      const onClick = vi.fn();
      render(
        <Card onClick={onClick}>
          <p>Clickable card</p>
        </Card>
      );

      const card = screen.getByText('Clickable card').parentElement;
      if (card) {
        fireEvent.click(card);
        expect(onClick).toHaveBeenCalledTimes(1);
      }
    });

    it('should have button role when onClick is provided', () => {
      render(
        <Card onClick={vi.fn()}>
          <p>Clickable card</p>
        </Card>
      );

      expect(screen.getByRole('button')).toBeInTheDocument();
    });

    it('should not have button role when onClick is not provided', () => {
      render(
        <Card>
          <p>Static card</p>
        </Card>
      );

      expect(screen.queryByRole('button')).not.toBeInTheDocument();
    });

    it('should apply cursor-pointer when onClick is provided', () => {
      render(
        <Card onClick={vi.fn()}>
          <p>Clickable card</p>
        </Card>
      );

      const card = screen.getByText('Clickable card').parentElement;
      expect(card).toHaveClass('cursor-pointer');
    });
  });

  describe('Keyboard Interaction', () => {
    it('should trigger onClick on Enter key', () => {
      const onClick = vi.fn();
      render(
        <Card onClick={onClick}>
          <p>Keyboard accessible card</p>
        </Card>
      );

      const card = screen.getByRole('button');
      fireEvent.keyDown(card, { key: 'Enter' });

      expect(onClick).toHaveBeenCalledTimes(1);
    });

    it('should trigger onClick on Space key', () => {
      const onClick = vi.fn();
      render(
        <Card onClick={onClick}>
          <p>Keyboard accessible card</p>
        </Card>
      );

      const card = screen.getByRole('button');
      fireEvent.keyDown(card, { key: ' ' });

      expect(onClick).toHaveBeenCalledTimes(1);
    });

    it('should not trigger onClick on other keys', () => {
      const onClick = vi.fn();
      render(
        <Card onClick={onClick}>
          <p>Keyboard accessible card</p>
        </Card>
      );

      const card = screen.getByRole('button');
      fireEvent.keyDown(card, { key: 'a' });
      fireEvent.keyDown(card, { key: 'Tab' });

      expect(onClick).not.toHaveBeenCalled();
    });

    it('should be focusable when onClick is provided', () => {
      render(
        <Card onClick={vi.fn()}>
          <p>Focusable card</p>
        </Card>
      );

      const card = screen.getByRole('button');
      expect(card).toHaveAttribute('tabIndex', '0');
    });

    it('should not have tabIndex when not clickable', () => {
      render(
        <Card>
          <p>Static card</p>
        </Card>
      );

      const card = screen.getByText('Static card').parentElement;
      expect(card).not.toHaveAttribute('tabIndex');
    });
  });

  describe('Styling', () => {
    it('should have default styling classes', () => {
      render(
        <Card>
          <p>Styled card</p>
        </Card>
      );

      const card = screen.getByText('Styled card').parentElement;
      expect(card).toHaveClass('bg-pf-panel');
      expect(card).toHaveClass('border');
      expect(card).toHaveClass('border-pf-border');
      expect(card).toHaveClass('rounded-lg');
      expect(card).toHaveClass('overflow-hidden');
    });

    it('should apply transition classes when interactive', () => {
      render(
        <Card onClick={vi.fn()}>
          <p>Interactive card</p>
        </Card>
      );

      const card = screen.getByText('Interactive card').parentElement;
      expect(card).toHaveClass('transition-all');
      expect(card).toHaveClass('duration-200');
    });
  });
});

describe('Card.Header', () => {
  it('should render header content', () => {
    render(
      <Card>
        <Card.Header>Header Content</Card.Header>
      </Card>
    );

    expect(screen.getByText('Header Content')).toBeInTheDocument();
  });

  it('should render header with actions', () => {
    render(
      <Card>
        <Card.Header actions={<button>Action</button>}>Header</Card.Header>
      </Card>
    );

    expect(screen.getByText('Header')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Action' })).toBeInTheDocument();
  });

  it('should apply custom className to header container', () => {
    render(
      <Card>
        <Card.Header className="custom-header">Header</Card.Header>
      </Card>
    );

    // The className is applied to the outer container div, not the inner text div
    const headerContainer = screen.getByText('Header').closest('.px-4.py-3');
    expect(headerContainer).toHaveClass('custom-header');
  });
});

describe('Card.Body', () => {
  it('should render body content', () => {
    render(
      <Card>
        <Card.Body>Body Content</Card.Body>
      </Card>
    );

    expect(screen.getByText('Body Content')).toBeInTheDocument();
  });

  it('should apply custom className to body', () => {
    render(
      <Card>
        <Card.Body className="custom-body">Body</Card.Body>
      </Card>
    );

    const body = screen.getByText('Body').closest('div');
    expect(body).toHaveClass('custom-body');
  });
});

describe('Card.Footer', () => {
  it('should render footer content', () => {
    render(
      <Card>
        <Card.Footer>Footer Content</Card.Footer>
      </Card>
    );

    expect(screen.getByText('Footer Content')).toBeInTheDocument();
  });

  it('should apply custom className to footer', () => {
    render(
      <Card>
        <Card.Footer className="custom-footer">Footer</Card.Footer>
      </Card>
    );

    const footer = screen.getByText('Footer').closest('div');
    expect(footer).toHaveClass('custom-footer');
  });
});

describe('Card Composition', () => {
  it('should render complete card with all sections', () => {
    render(
      <Card>
        <Card.Header>Card Title</Card.Header>
        <Card.Body>Card content goes here</Card.Body>
        <Card.Footer>
          <button>Cancel</button>
          <button>Save</button>
        </Card.Footer>
      </Card>
    );

    expect(screen.getByText('Card Title')).toBeInTheDocument();
    expect(screen.getByText('Card content goes here')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Cancel' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save' })).toBeInTheDocument();
  });
});
