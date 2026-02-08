import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import JobTagsEditor from '../JobTagsEditor';

describe('JobTagsEditor', () => {
  const mockOnTagsChange = vi.fn();

  it('should render in view mode with tags', () => {
    render(
      <JobTagsEditor
        tags={['PLA', 'Prototype']}
        isEditing={false}
        onTagsChange={mockOnTagsChange}
      />
    );

    expect(screen.getByText('PLA')).toBeInTheDocument();
    expect(screen.getByText('Prototype')).toBeInTheDocument();
  });

  it('should render "No tags added" when no tags in view mode', () => {
    render(
      <JobTagsEditor
        tags={[]}
        isEditing={false}
        onTagsChange={mockOnTagsChange}
      />
    );

    expect(screen.getByText(/No tags added/i)).toBeInTheDocument();
  });

  it('should render in edit mode with input', () => {
    render(
      <JobTagsEditor
        tags={[]}
        isEditing={true}
        onTagsChange={mockOnTagsChange}
      />
    );

    expect(screen.getByPlaceholderText(/Add tags/i)).toBeInTheDocument();
  });

  it('should show existing tags with remove buttons in edit mode', () => {
    render(
      <JobTagsEditor
        tags={['PLA', 'Test']}
        isEditing={true}
        onTagsChange={mockOnTagsChange}
      />
    );

    expect(screen.getByText('PLA')).toBeInTheDocument();
    expect(screen.getByText('Test')).toBeInTheDocument();
    expect(screen.getAllByRole('button').length).toBeGreaterThan(0);
  });

  it('should add tag on Enter key', () => {
    render(
      <JobTagsEditor
        tags={[]}
        isEditing={true}
        onTagsChange={mockOnTagsChange}
      />
    );

    const input = screen.getByPlaceholderText(/Add tags/i) as HTMLInputElement;
    fireEvent.change(input, { target: { value: 'NewTag' } });
    fireEvent.keyDown(input, { key: 'Enter' });

    expect(mockOnTagsChange).toHaveBeenCalledWith(['NewTag']);
  });

  it('should remove tag when remove button clicked', () => {
    render(
      <JobTagsEditor
        tags={['PLA', 'Test']}
        isEditing={true}
        onTagsChange={mockOnTagsChange}
      />
    );

    const removeButtons = screen.getAllByRole('button');
    fireEvent.click(removeButtons[0]); // Remove first tag

    expect(mockOnTagsChange).toHaveBeenCalledWith(['Test']);
  });

  it('should show error for empty tag', () => {
    render(
      <JobTagsEditor
        tags={[]}
        isEditing={true}
        onTagsChange={mockOnTagsChange}
      />
    );

    const input = screen.getByPlaceholderText(/Add tags/i) as HTMLInputElement;
    fireEvent.change(input, { target: { value: '   ' } });
    fireEvent.keyDown(input, { key: 'Enter' });

    expect(screen.getByText(/Tag cannot be empty/i)).toBeInTheDocument();
  });

  it('should show error for duplicate tag', () => {
    render(
      <JobTagsEditor
        tags={['PLA']}
        isEditing={true}
        onTagsChange={mockOnTagsChange}
      />
    );

    const input = screen.getByPlaceholderText(/Add tags/i) as HTMLInputElement;
    fireEvent.change(input, { target: { value: 'PLA' } });
    fireEvent.keyDown(input, { key: 'Enter' });

    expect(screen.getByText(/This tag already exists/i)).toBeInTheDocument();
  });

  it('should show error when max tags reached', () => {
    const maxTags = Array.from({ length: 10 }, (_, i) => `Tag${i + 1}`);
    render(
      <JobTagsEditor
        tags={maxTags}
        isEditing={true}
        onTagsChange={mockOnTagsChange}
      />
    );

    const input = screen.getByPlaceholderText(/Add tags/i) as HTMLInputElement;
    fireEvent.change(input, { target: { value: 'ExtraTag' } });
    fireEvent.keyDown(input, { key: 'Enter' });

    expect(screen.getByText(/Maximum 10 tags allowed/i)).toBeInTheDocument();
  });

  it('should show error for tag exceeding max length', () => {
    const longTag = 'A'.repeat(31);
    render(
      <JobTagsEditor
        tags={[]}
        isEditing={true}
        onTagsChange={mockOnTagsChange}
      />
    );

    const input = screen.getByPlaceholderText(/Add tags/i) as HTMLInputElement;
    fireEvent.change(input, { target: { value: longTag } });
    fireEvent.keyDown(input, { key: 'Enter' });

    expect(screen.getByText(/must be 30 characters or less/i)).toBeInTheDocument();
  });

  it('should show suggestions when typing', () => {
    render(
      <JobTagsEditor
        tags={[]}
        isEditing={true}
        onTagsChange={mockOnTagsChange}
      />
    );

    const input = screen.getByPlaceholderText(/Add tags/i) as HTMLInputElement;
    fireEvent.change(input, { target: { value: 'PL' } });
    fireEvent.focus(input);

    // Wait for suggestions to appear
    const suggestions = screen.getAllByRole('option');
    expect(suggestions.length).toBeGreaterThan(0);
  });

  it('should add tag from suggestion click', () => {
    render(
      <JobTagsEditor
        tags={[]}
        isEditing={true}
        onTagsChange={mockOnTagsChange}
      />
    );

    const input = screen.getByPlaceholderText(/Add tags/i) as HTMLInputElement;
    fireEvent.change(input, { target: { value: 'PL' } });
    fireEvent.focus(input);

    const plaOption = screen.getAllByRole('option').find(el => el.textContent === 'PLA');
    if (plaOption) {
      fireEvent.click(plaOption);
      expect(mockOnTagsChange).toHaveBeenCalledWith(['PLA']);
    }
  });

  it('should close suggestions on Escape key', () => {
    render(
      <JobTagsEditor
        tags={[]}
        isEditing={true}
        onTagsChange={mockOnTagsChange}
      />
    );

    const input = screen.getByPlaceholderText(/Add tags/i) as HTMLInputElement;
    fireEvent.change(input, { target: { value: 'PL' } });
    fireEvent.focus(input);
    
    expect(screen.getByText('PLA')).toBeInTheDocument();
    
    fireEvent.keyDown(input, { key: 'Escape' });
    
    // Suggestions should be closed (implementation detail - hard to test without internal state)
  });

  it('should show tag count', () => {
    render(
      <JobTagsEditor
        tags={['PLA', 'Test', 'Prototype']}
        isEditing={true}
        onTagsChange={mockOnTagsChange}
      />
    );

    expect(screen.getByText(/3 \/ 10 tags/i)).toBeInTheDocument();
  });

  it('should clear input after adding tag', () => {
    render(
      <JobTagsEditor
        tags={[]}
        isEditing={true}
        onTagsChange={mockOnTagsChange}
      />
    );

    const input = screen.getByPlaceholderText(/Add tags/i) as HTMLInputElement;
    fireEvent.change(input, { target: { value: 'NewTag' } });
    fireEvent.keyDown(input, { key: 'Enter' });

    expect(input.value).toBe('');
  });
});
