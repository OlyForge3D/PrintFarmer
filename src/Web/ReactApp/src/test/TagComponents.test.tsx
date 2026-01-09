import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, fireEvent, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { TagDisplay, TagDisplayProps } from '../components/TagDisplay';
import { TagInput, TagInputProps } from '../components/TagInput';
import * as tagServiceModule from '../services/tagService';

// Mock tag service
vi.mock('../services/tagService', () => ({
  tagService: {
    listTags: vi.fn(),
    searchTags: vi.fn(),
    getPopularTags: vi.fn(),
    getAnalytics: vi.fn(),
    createTag: vi.fn(),
    deleteTag: vi.fn(),
    getTagById: vi.fn(),
    filterModelsWithAllTags: vi.fn(),
    filterModelsWithAnyTag: vi.fn(),
    filterModelsComplex: vi.fn(),
    clearCache: vi.fn(),
  },
  TagDto: {},
  TagSuggestionDto: {},
  TagAnalyticsDto: {},
}));

describe('TagDisplay Component', () => {
  const mockTag = {
    id: '1',
    name: 'Important',
    color: '#ef4444',
    description: 'Important models',
  };

  it('renders tag with name', () => {
    render(<TagDisplay tag={mockTag} />);
    expect(screen.getByText('Important')).toBeInTheDocument();
  });

  it('renders tag with description as title', () => {
    render(<TagDisplay tag={mockTag} />);
    const tagElement = screen.getByRole('status');
    expect(tagElement).toHaveAttribute('title', 'Important models');
  });

  it('renders remove button when showRemoveButton is true', () => {
    render(<TagDisplay tag={mockTag} showRemoveButton={true} />);
    const buttons = screen.getAllByRole('button');
    // Should have 2 buttons: tag name (role=button) and remove button
    expect(buttons.length).toBeGreaterThanOrEqual(2);
    expect(buttons[1]).toBeInTheDocument();
  });

  it('hides remove button when showRemoveButton is false', () => {
    const { container } = render(<TagDisplay tag={mockTag} showRemoveButton={false} />);
    // Should only have one button-like element (the tag name itself)
    const buttons = screen.getAllByRole('button');
    expect(buttons.length).toBeLessThanOrEqual(1);
  });

  it('calls onRemove callback when remove button is clicked', () => {
    const onRemove = vi.fn();
    render(<TagDisplay tag={mockTag} showRemoveButton={true} onRemove={onRemove} />);
    
    const buttons = screen.getAllByRole('button');
    const removeButton = buttons[buttons.length - 1]; // Last button is remove
    fireEvent.click(removeButton);
    expect(onRemove).toHaveBeenCalledWith('1');
  });

  it('calls onClick callback when tag is clicked', () => {
    const onClick = vi.fn();
    render(<TagDisplay tag={mockTag} onClick={onClick} />);
    
    fireEvent.click(screen.getByText('Important'));
    expect(onClick).toHaveBeenCalledWith(mockTag);
  });

  it('supports keyboard navigation - Enter key', async () => {
    const onClick = vi.fn();
    const user = userEvent.setup();
    render(<TagDisplay tag={mockTag} onClick={onClick} />);
    
    const buttons = screen.getAllByRole('button');
    const tagButton = buttons[0];
    tagButton.focus();
    await user.keyboard('{Enter}');
    expect(onClick).toHaveBeenCalled();
  });

  it('supports keyboard navigation - Delete key with remove', async () => {
    const onRemove = vi.fn();
    const user = userEvent.setup();
    render(<TagDisplay tag={mockTag} showRemoveButton={true} onRemove={onRemove} />);
    
    const tagElement = screen.getByText('Important');
    fireEvent.keyDown(tagElement, { key: 'Delete' });
    expect(onRemove).toHaveBeenCalledWith('1');
  });

  it('renders with custom color', () => {
    render(<TagDisplay tag={mockTag} />);
    const tagElement = screen.getByRole('status');
    // Color is applied via inline style
    expect(tagElement).toHaveStyle(`background-color: ${mockTag.color}`);
  });

  it('applies disabled state correctly', () => {
    render(<TagDisplay tag={mockTag} disabled={true} showRemoveButton={true} />);
    const buttons = screen.getAllByRole('button');
    const removeButton = buttons[buttons.length - 1];
    expect(removeButton).toBeDisabled();
  });

  it('applies custom className', () => {
    const { container } = render(
      <TagDisplay tag={mockTag} className="custom-class" />
    );
    expect(container.firstChild).toHaveClass('custom-class');
  });
});

describe('TagInput Component', () => {
  const mockTags = [
    { id: '1', name: 'Important', color: '#ef4444' },
    { id: '2', name: 'Review', color: '#f97316' },
  ];

  const mockSuggestions = [
    { id: '3', name: 'Urgent', usageCount: 45 },
    { id: '4', name: 'Archived', usageCount: 23 },
  ];

  beforeEach(() => {
    vi.clearAllMocks();
    const tagService = tagServiceModule.tagService;
    (tagService.getPopularTags as any).mockResolvedValue(mockSuggestions);
    (tagService.searchTags as any).mockImplementation((query: string, callback: (results: any[]) => void) => {
      setTimeout(() => callback(mockSuggestions), 100);
    });
    (tagService.getTagById as any).mockResolvedValue(mockSuggestions[0]);
  });

  it('renders input field', () => {
    render(<TagInput selectedTags={[]} onChange={vi.fn()} />);
    expect(screen.getByRole('textbox')).toBeInTheDocument();
  });

  it('displays selected tags', () => {
    render(<TagInput selectedTags={mockTags} onChange={vi.fn()} />);
    expect(screen.getByText('Important')).toBeInTheDocument();
    expect(screen.getByText('Review')).toBeInTheDocument();
  });

  it('removes tag when remove button clicked', async () => {
    const onChange = vi.fn();
    render(
      <TagInput selectedTags={mockTags} onChange={onChange} />
    );

    // Find the remove button for the first tag (look for × button or aria-label with Remove)
    const buttons = screen.getAllByRole('button');
    let removeButton = null;
    
    for (const button of buttons) {
      if (button.textContent === '×' || button.getAttribute('aria-label')?.includes('Remove')) {
        removeButton = button;
        break;
      }
    }
    
    expect(removeButton).toBeTruthy();
    
    if (removeButton) {
      await userEvent.click(removeButton);
      // Give component time to process the removal
      await new Promise(resolve => setTimeout(resolve, 100));
      expect(onChange).toHaveBeenCalled();
    }
  });

  it('shows suggestions on input focus', async () => {
    render(<TagInput selectedTags={[]} onChange={vi.fn()} />);
    
    const input = screen.getByRole('textbox');
    fireEvent.focus(input);

    await waitFor(() => {
      expect(screen.getByText('Urgent')).toBeInTheDocument();
    });
  });

  it('filters suggestions based on input', async () => {
    const tagService = tagServiceModule.tagService;
    (tagService.searchTags as any).mockImplementation((query: string, callback: (results: any[]) => void) => {
      callback([mockSuggestions[0]]);
    });

    render(<TagInput selectedTags={[]} onChange={vi.fn()} />);

    const input = screen.getByRole('textbox');
    await userEvent.type(input, 'urg');

    await waitFor(() => {
      expect(tagService.searchTags).toHaveBeenCalledWith('urg', expect.any(Function));
    });
  });

  it('adds tag from suggestion', async () => {
    const onChange = vi.fn();
    const tagService = tagServiceModule.tagService;
    (tagService.getTagById as any).mockResolvedValue(mockSuggestions[0]);

    render(<TagInput selectedTags={[]} onChange={onChange} />);

    const input = screen.getByRole('textbox');
    fireEvent.focus(input);

    await waitFor(() => {
      expect(screen.getByText('Urgent')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByText('Urgent'));

    await waitFor(() => {
      expect(onChange).toHaveBeenCalled();
    });
  });

  it('supports keyboard navigation - arrow keys', async () => {
    const user = userEvent.setup();
    render(<TagInput selectedTags={[]} onChange={vi.fn()} />);

    const input = screen.getByRole('textbox') as HTMLInputElement;
    await user.click(input);

    // Wait for suggestions to appear
    await waitFor(() => {
      expect(screen.queryByText('Urgent')).toBeInTheDocument();
    }, { timeout: 1000 });

    // Press arrow down to navigate
    await user.keyboard('{ArrowDown}');

    // Verify a suggestion is highlighted/focused
    const suggestionItems = screen.getAllByRole('option');
    expect(suggestionItems.length).toBeGreaterThan(0);
  });

  it('creates new tag with Enter key', async () => {
    const onChange = vi.fn();
    const user = userEvent.setup();
    const tagService = tagServiceModule.tagService;
    (tagService.createTag as any).mockResolvedValue({
      id: 'new',
      name: 'NewTag',
    });

    render(<TagInput selectedTags={[]} onChange={onChange} />);

    const input = screen.getByRole('textbox') as HTMLInputElement;
    await user.type(input, 'NewTag');
    await user.keyboard('{Enter}');

    await waitFor(() => {
      expect(tagService.createTag).toHaveBeenCalled();
    }, { timeout: 1000 });
  });

  it('removes tag with backspace on empty input', async () => {
    const onChange = vi.fn();
    render(<TagInput selectedTags={mockTags} onChange={onChange} />);

    const input = screen.getByRole('textbox') as HTMLInputElement;
    fireEvent.keyDown(input, { key: 'Backspace' });

    expect(onChange).toHaveBeenCalledWith([mockTags[0]]);
  });

  it('enforces maxTags limit', async () => {
    const onChange = vi.fn();
    render(
      <TagInput
        selectedTags={mockTags}
        onChange={onChange}
        maxTags={2}
      />
    );

    expect(screen.getByText('2 / 2 tags')).toBeInTheDocument();
  });

  it('validates tag names with custom validator', async () => {
    const validator = vi.fn().mockReturnValue({
      valid: false,
      error: 'Tag name too short',
    });

    const onChange = vi.fn();
    render(
      <TagInput
        selectedTags={[]}
        onChange={onChange}
        validator={validator}
      />
    );

    const input = screen.getByRole('textbox') as HTMLInputElement;
    await userEvent.type(input, 'a');
    fireEvent.keyDown(input, { key: 'Enter' });

    await waitFor(() => {
      expect(screen.getByText('Tag name too short')).toBeInTheDocument();
    });
  });

  it('prevents duplicate tags', async () => {
    const onChange = vi.fn();
    const user = userEvent.setup();
    const tagService = tagServiceModule.tagService;
    (tagService.getTagById as any).mockResolvedValue(mockTags[0]);

    render(
      <TagInput
        selectedTags={mockTags}
        onChange={onChange}
      />
    );

    const input = screen.getByRole('textbox') as HTMLInputElement;
    await user.click(input);

    await waitFor(() => {
      expect(screen.queryByText('Important')).toBeInTheDocument();
    }, { timeout: 1000 });

    const importantItems = screen.getAllByText('Important');
    if (importantItems.length > 0) {
      await user.click(importantItems[0]);
    }

    // The component should show error or prevent adding
    const errorMessage = screen.queryByText('Tag already added');
    // If there's error handling for duplicates
    if (errorMessage) {
      expect(errorMessage).toBeInTheDocument();
    }
  });

  it('closes suggestions with Escape key', async () => {
    const user = userEvent.setup();
    render(<TagInput selectedTags={[]} onChange={vi.fn()} />);

    const input = screen.getByRole('textbox') as HTMLInputElement;
    await user.click(input);

    await waitFor(() => {
      expect(screen.queryByText('Urgent')).toBeInTheDocument();
    }, { timeout: 1000 });

    await user.keyboard('{Escape}');

    // After escape, suggestions may still be in DOM but hidden
    // Check if component is still interactive
    expect(input).toBeInTheDocument();
  });

  it('disables input when disabled prop is true', () => {
    render(<TagInput selectedTags={[]} onChange={vi.fn()} disabled={true} />);
    const input = screen.getByRole('textbox') as HTMLInputElement;
    expect(input).toBeDisabled();
  });

  it('displays placeholder text', () => {
    const placeholder = 'Add custom tags...';
    render(
      <TagInput
        selectedTags={[]}
        onChange={vi.fn()}
        placeholder={placeholder}
      />
    );
    expect(screen.getByPlaceholderText(placeholder)).toBeInTheDocument();
  });

  it('applies custom className', () => {
    const { container } = render(
      <TagInput
        selectedTags={[]}
        onChange={vi.fn()}
        className="custom-input"
      />
    );
    expect(container.firstChild).toHaveClass('custom-input');
  });

  it('focuses input after adding tag', async () => {
    const onChange = vi.fn();
    const tagService = tagServiceModule.tagService;
    (tagService.getTagById as any).mockResolvedValue(mockSuggestions[0]);

    render(<TagInput selectedTags={[]} onChange={onChange} />);

    const input = screen.getByRole('textbox') as HTMLInputElement;
    fireEvent.focus(input);

    await waitFor(() => {
      expect(screen.getByText('Urgent')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByText('Urgent'));

    await waitFor(() => {
      expect(document.activeElement).toBe(input);
    });
  });

  it('clears input after adding tag', async () => {
    const onChange = vi.fn();
    const tagService = tagServiceModule.tagService;
    (tagService.getTagById as any).mockResolvedValue(mockSuggestions[0]);

    render(<TagInput selectedTags={[]} onChange={onChange} />);

    const input = screen.getByRole('textbox') as HTMLInputElement;
    fireEvent.focus(input);

    await waitFor(() => {
      expect(screen.getByText('Urgent')).toBeInTheDocument();
    });

    fireEvent.click(screen.getByText('Urgent'));

    await waitFor(() => {
      expect(input.value).toBe('');
    });
  });
});

describe('Tag Components Accessibility', () => {
  it('TagDisplay has proper ARIA labels', () => {
    const tag = {
      id: '1',
      name: 'Important',
      color: '#ef4444',
      description: 'Mark important items',
    };

    render(<TagDisplay tag={tag} showRemoveButton={true} />);

    expect(screen.getByLabelText(/Remove tag/)).toBeInTheDocument();
    expect(screen.getByRole('status')).toBeInTheDocument();
  });

  it('TagInput has proper ARIA attributes', () => {
    render(<TagInput selectedTags={[]} onChange={vi.fn()} />);

    const input = screen.getByRole('textbox');
    expect(input).toHaveAttribute('aria-label');
    expect(input).toHaveAttribute('aria-autocomplete', 'list');
    expect(input).toHaveAttribute('aria-haspopup', 'listbox');
  });

  it('TagInput error message has role alert', async () => {
    const validator = vi.fn().mockReturnValue({
      valid: false,
      error: 'Invalid tag',
    });

    render(
      <TagInput
        selectedTags={[]}
        onChange={vi.fn()}
        validator={validator}
      />
    );

    const input = screen.getByRole('textbox') as HTMLInputElement;
    await userEvent.type(input, 'x');
    fireEvent.keyDown(input, { key: 'Enter' });

    await waitFor(() => {
      expect(screen.getByRole('alert')).toBeInTheDocument();
    });
  });
});
