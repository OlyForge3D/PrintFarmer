import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ModelSelector } from '@/features/slicer/components/job/ModelSelector';
import type { Model3DBasic } from '@/features/slicer/components/job/types';

describe('ModelSelector - Display Name Regression', () => {
  const mockModels: Model3DBasic[] = [
    { id: '1', originalFileName: 'my-awesome-dragon.stl' },
    { id: '2', originalFileName: 'calibration-cube.stl' },
    { id: '3', originalFileName: 'benchy-v2-final-REAL.stl' },
  ];

  it('displays original file names in model picker dropdown', () => {
    // Arrange: Component receives models with original file names
    const onModelIdChange = vi.fn();
    const onToggleMode = vi.fn();

    // Act: Render the model picker
    render(
      <ModelSelector
        useModelPicker={true}
        onToggleMode={onToggleMode}
        models={mockModels}
        isLoadingModels={false}
        modelsError={null}
        selectedModelId=""
        onModelIdChange={onModelIdChange}
        fileUrl=""
        onFileUrlChange={vi.fn()}
        fileName=""
        onFileNameChange={vi.fn()}
      />
    );

    // Assert: All original file names are visible in the dropdown
    expect(screen.getByRole('combobox')).toBeInTheDocument();

    // Check each model's original name is in the options
    const options = screen.getAllByRole('option');
    expect(options).toHaveLength(4); // 3 models + "Select model" placeholder

    expect(screen.getByRole('option', { name: 'my-awesome-dragon.stl' })).toBeInTheDocument();
    expect(screen.getByRole('option', { name: 'calibration-cube.stl' })).toBeInTheDocument();
    expect(screen.getByRole('option', { name: 'benchy-v2-final-REAL.stl' })).toBeInTheDocument();
  });

  it('calls onModelIdChange with correct ID when user selects model by original name', async () => {
    // Arrange
    const onModelIdChange = vi.fn();
    const user = userEvent.setup();

    render(
      <ModelSelector
        useModelPicker={true}
        onToggleMode={vi.fn()}
        models={mockModels}
        selectedModelId=""
        onModelIdChange={onModelIdChange}
        fileUrl=""
        onFileUrlChange={vi.fn()}
        fileName=""
        onFileNameChange={vi.fn()}
      />
    );

    // Act: User selects "calibration-cube.stl" from dropdown
    const select = screen.getByRole('combobox');
    await user.selectOptions(select, '2');

    // Assert: Callback receives the correct model ID
    expect(onModelIdChange).toHaveBeenCalledWith('2');
    expect(onModelIdChange).toHaveBeenCalledTimes(1);
  });

  it('shows selected model with original file name visible', () => {
    // Arrange: Pre-select a model
    render(
      <ModelSelector
        useModelPicker={true}
        onToggleMode={vi.fn()}
        models={mockModels}
        selectedModelId="3"
        onModelIdChange={vi.fn()}
        fileUrl=""
        onFileUrlChange={vi.fn()}
        fileName=""
        onFileNameChange={vi.fn()}
      />
    );

    // Assert: Selected option displays original file name
    const select = screen.getByRole('combobox') as HTMLSelectElement;
    expect(select.value).toBe('3');

    const selectedOption = screen.getByRole('option', { name: 'benchy-v2-final-REAL.stl' }) as HTMLOptionElement;
    expect(selectedOption.selected).toBe(true);
  });

  it('handles models with no originalFileName gracefully', () => {
    // Arrange: Edge case - model missing originalFileName (should not crash)
    const edgeCaseModels = [
      { id: '1', originalFileName: 'valid-name.stl' },
      { id: '2', originalFileName: '' }, // Empty name
    ] as Model3DBasic[];

    // Act & Assert: Should render without crashing
    render(
      <ModelSelector
        useModelPicker={true}
        onToggleMode={vi.fn()}
        models={edgeCaseModels}
        selectedModelId=""
        onModelIdChange={vi.fn()}
        fileUrl=""
        onFileUrlChange={vi.fn()}
        fileName=""
        onFileNameChange={vi.fn()}
      />
    );

    expect(screen.getByRole('combobox')).toBeInTheDocument();
    expect(screen.getByRole('option', { name: 'valid-name.stl' })).toBeInTheDocument();
  });

  it('displays loading state without showing model names', () => {
    // Arrange: Loading state
    render(
      <ModelSelector
        useModelPicker={true}
        onToggleMode={vi.fn()}
        models={undefined}
        isLoadingModels={true}
        modelsError={null}
        selectedModelId=""
        onModelIdChange={vi.fn()}
        fileUrl=""
        onFileUrlChange={vi.fn()}
        fileName=""
        onFileNameChange={vi.fn()}
      />
    );

    // Assert: Shows loading text, not model names
    expect(screen.getByRole('combobox')).toBeDisabled();
    expect(screen.getByText('Loading models...')).toBeInTheDocument();
    expect(screen.queryByText('my-awesome-dragon.stl')).not.toBeInTheDocument();
  });

  it('displays error state when models fail to load', () => {
    // Arrange: Error state
    const error = new Error('Failed to fetch models from API');

    render(
      <ModelSelector
        useModelPicker={true}
        onToggleMode={vi.fn()}
        models={undefined}
        isLoadingModels={false}
        modelsError={error}
        selectedModelId=""
        onModelIdChange={vi.fn()}
        fileUrl=""
        onFileUrlChange={vi.fn()}
        fileName=""
        onFileNameChange={vi.fn()}
      />
    );

    // Assert: Shows error message
    expect(screen.getByText(error.message)).toBeInTheDocument();
    expect(screen.queryByRole('combobox')).not.toBeInTheDocument();
  });

  it('displays empty state when no models available', () => {
    // Arrange: Empty model list
    render(
      <ModelSelector
        useModelPicker={true}
        onToggleMode={vi.fn()}
        models={[]}
        isLoadingModels={false}
        modelsError={null}
        selectedModelId=""
        onModelIdChange={vi.fn()}
        fileUrl=""
        onFileUrlChange={vi.fn()}
        fileName=""
        onFileNameChange={vi.fn()}
      />
    );

    // Assert: Shows empty message
    expect(screen.getByRole('combobox')).toBeDisabled();
    expect(screen.getByText('-- No models available --')).toBeInTheDocument();
  });

  it('allows toggling between picker and manual URL input', async () => {
    // Arrange
    const onToggleMode = vi.fn();
    const user = userEvent.setup();

    render(
      <ModelSelector
        useModelPicker={true}
        onToggleMode={onToggleMode}
        models={mockModels}
        selectedModelId=""
        onModelIdChange={vi.fn()}
        fileUrl=""
        onFileUrlChange={vi.fn()}
        fileName=""
        onFileNameChange={vi.fn()}
      />
    );

    // Act: Click toggle button
    const toggleButton = screen.getByRole('button', { name: /enter url manually/i });
    await user.click(toggleButton);

    // Assert: Toggle callback fired
    expect(onToggleMode).toHaveBeenCalledTimes(1);
  });

  it('shows manual URL inputs when picker mode is disabled', () => {
    // Arrange: Manual mode
    render(
      <ModelSelector
        useModelPicker={false}
        onToggleMode={vi.fn()}
        models={mockModels}
        selectedModelId=""
        onModelIdChange={vi.fn()}
        fileUrl="https://example.com/model.stl"
        onFileUrlChange={vi.fn()}
        fileName="external-model.stl"
        onFileNameChange={vi.fn()}
      />
    );

    // Assert: URL and filename inputs are visible, picker is not
    expect(screen.getByPlaceholderText('https://... or /storage/...')).toBeInTheDocument();
    expect(screen.getByPlaceholderText('model.stl')).toBeInTheDocument();
    expect(screen.queryByRole('combobox')).not.toBeInTheDocument();
  });

  it('does not render GUID storage names in picker options', () => {
    // Arrange: Models that should NOT show internal storage names
    const modelsWithStorage = [
      { id: 'abc-123-def', originalFileName: 'user-friendly-name.stl' },
    ] as Model3DBasic[];

    render(
      <ModelSelector
        useModelPicker={true}
        onToggleMode={vi.fn()}
        models={modelsWithStorage}
        selectedModelId=""
        onModelIdChange={vi.fn()}
        fileUrl=""
        onFileUrlChange={vi.fn()}
        fileName=""
        onFileNameChange={vi.fn()}
      />
    );

    // Assert: Only user-friendly name is visible, not the ID
    expect(screen.getByRole('option', { name: 'user-friendly-name.stl' })).toBeInTheDocument();
    expect(screen.queryByText('abc-123-def')).not.toBeInTheDocument();
  });
});
