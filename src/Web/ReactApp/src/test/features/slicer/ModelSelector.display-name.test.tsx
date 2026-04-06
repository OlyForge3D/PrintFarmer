import { describe, it, expect, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ModelSelector } from '@/features/slicer/components/job/ModelSelector';
import type { Model3DBasic } from '@/features/slicer/components/job/types';

describe('ModelSelector - Display Name Regression', () => {
  const mockModels: Model3DBasic[] = [
    { id: '1', originalFileName: 'my-awesome-dragon.stl' },
    { id: '2', originalFileName: 'calibration-cube.stl' },
    { id: '3', originalFileName: 'benchy-v2-final-REAL.stl' },
  ];

  it('displays original file names in model picker modal', async () => {
    const user = userEvent.setup();

    render(
      <ModelSelector
        useModelPicker={true}
        onToggleMode={vi.fn()}
        models={mockModels}
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

    // Click the picker button to open the modal
    await user.click(screen.getByText('Select a model...'));

    // All original file names should be visible in the modal
    expect(screen.getByText('my-awesome-dragon.stl')).toBeInTheDocument();
    expect(screen.getByText('calibration-cube.stl')).toBeInTheDocument();
    expect(screen.getByText('benchy-v2-final-REAL.stl')).toBeInTheDocument();
  });

  it('calls onModelIdChange with correct ID when user selects model', async () => {
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

    // Open the picker
    await user.click(screen.getByText('Select a model...'));

    // Double-click on "calibration-cube.stl" to select it
    await user.dblClick(screen.getByText('calibration-cube.stl'));

    expect(onModelIdChange).toHaveBeenCalledWith('2');
  });

  it('shows selected model with original file name visible', () => {
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

    // The picker button should display the selected model's original file name
    expect(screen.getByText('benchy-v2-final-REAL.stl')).toBeInTheDocument();
  });

  it('handles models with no originalFileName gracefully', () => {
    const edgeCaseModels = [
      { id: '1', originalFileName: 'valid-name.stl' },
      { id: '2', originalFileName: '' },
    ] as Model3DBasic[];

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

    // Should render without crashing; shows picker button
    expect(screen.getByText('Select a model...')).toBeInTheDocument();
  });

  it('displays loading state in modal when models are loading', async () => {
    const user = userEvent.setup();

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

    // Open picker — it should still be clickable
    await user.click(screen.getByText('Select a model...'));

    // Should not show model names
    expect(screen.queryByText('my-awesome-dragon.stl')).not.toBeInTheDocument();
  });

  it('displays error state when models fail to load', () => {
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

    expect(screen.getByText(error.message)).toBeInTheDocument();
  });

  it('displays empty state when no models available', async () => {
    const user = userEvent.setup();

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

    // Open the picker modal
    await user.click(screen.getByText('Select a model...'));

    // Should show empty message
    expect(screen.getByText('No models match your search.')).toBeInTheDocument();
  });

  it('allows toggling between picker and manual URL input', async () => {
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

    const toggleButton = screen.getByRole('button', { name: /enter url manually/i });
    await user.click(toggleButton);

    expect(onToggleMode).toHaveBeenCalledTimes(1);
  });

  it('shows manual URL inputs when picker mode is disabled', () => {
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

    expect(screen.getByPlaceholderText('https://... or /storage/...')).toBeInTheDocument();
    expect(screen.getByPlaceholderText('model.stl')).toBeInTheDocument();
  });

  it('does not render GUID storage names in picker', async () => {
    const user = userEvent.setup();
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

    // Open the picker
    await user.click(screen.getByText('Select a model...'));

    // Only user-friendly name is visible, not the ID
    expect(screen.getByText('user-friendly-name.stl')).toBeInTheDocument();
    // The modal might contain the ID internally but it shouldn't be the visible label text
    const listbox = screen.getByRole('listbox');
    expect(within(listbox).queryByText('abc-123-def')).not.toBeInTheDocument();
  });

  it('opens picker modal when pickerOpen prop is true', () => {
    render(
      <ModelSelector
        useModelPicker={true}
        onToggleMode={vi.fn()}
        models={mockModels}
        selectedModelId=""
        onModelIdChange={vi.fn()}
        fileUrl=""
        onFileUrlChange={vi.fn()}
        fileName=""
        onFileNameChange={vi.fn()}
        pickerOpen={true}
        onPickerOpenChange={vi.fn()}
      />
    );

    // Modal should be open showing all models
    expect(screen.getByText('my-awesome-dragon.stl')).toBeInTheDocument();
    expect(screen.getByText('calibration-cube.stl')).toBeInTheDocument();
    expect(screen.getByText('benchy-v2-final-REAL.stl')).toBeInTheDocument();
  });
});
