import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { createRef } from 'react';
import { ModelAliasEditor, type ModelAliasEditorRef } from '@/features/catalog/components/ModelAliasEditor';

const { getAliases, updateAliases } = vi.hoisted(() => ({ getAliases: vi.fn(), updateAliases: vi.fn() }));
vi.mock('@/services/api', () => ({ apiClient: { getModelAliases: getAliases, updateModelAliases: updateAliases } }));

function addAlias(name: string, method: 'click' | 'enter' = 'click') {
  const input = screen.getByPlaceholderText('Model name in slicer...');
  fireEvent.change(input, { target: { value: name } });
  if (method === 'enter') fireEvent.keyDown(input, { key: 'Enter' });
  else fireEvent.click(screen.getByTitle('Add alias'));
}

describe('ModelAliasEditor HTTP LAN identities', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    getAliases.mockResolvedValue([]);
    vi.stubGlobal('crypto', { getRandomValues: vi.fn(crypto.getRandomValues.bind(crypto)) });
  });
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('preserves stored and temporary identities while editing and retrying a names-only save', async () => {
    getAliases.mockResolvedValue([{ id: 'legacy-saved-id', slicerModelName: 'Existing alias', slicerType: 'OrcaSlicer' }]);
    const failure = new Error('Network Error');
    updateAliases.mockRejectedValueOnce(failure).mockResolvedValueOnce([]);
    vi.spyOn(console, 'error').mockImplementation(() => {});
    const ref = createRef<ModelAliasEditorRef>();
    render(<ModelAliasEditor ref={ref} modelId="model-1" />);
    const existing = await screen.findByText('Existing alias');
    expect(crypto.getRandomValues).not.toHaveBeenCalled();

    addAlias('First alias');
    const first = screen.getByText('First alias');
    addAlias('Second alias', 'enter');
    expect(screen.getByText('Existing alias')).toBe(existing);
    expect(screen.getByText('First alias')).toBe(first);
    expect(crypto.getRandomValues).toHaveBeenCalledTimes(2);
    fireEvent.click(within(screen.getByText('Second alias').parentElement!).getByTitle('Delete alias'));
    expect(screen.queryByText('Second alias')).not.toBeInTheDocument();
    expect(screen.getByText('First alias')).toBe(first);

    await act(async () => { await expect(ref.current!.saveChanges()).rejects.toBe(failure); });
    expect(screen.getByText('Failed to save aliases')).toBeInTheDocument();
    expect(screen.getByText('First alias')).toBe(first);
    expect(crypto.getRandomValues).toHaveBeenCalledTimes(2);
    // Saving/retrying existing entries must not need another random ID.
    vi.stubGlobal('crypto', {});
    await act(async () => { await ref.current!.saveChanges(); });
    expect(updateAliases).toHaveBeenCalledTimes(2);
    for (const call of updateAliases.mock.calls) {
      expect(call).toEqual(['model-1', { orcaSlicerNames: ['Existing alias', 'First alias'], prusaSlicerNames: [] }]);
    }
  });

  it.each(['click', 'enter'] as const)('surfaces secure ID failure on %s without losing input or adding a phantom alias', async (method) => {
    const secureCrypto = crypto;
    vi.stubGlobal('crypto', {});
    const ref = createRef<ModelAliasEditorRef>();
    render(<ModelAliasEditor ref={ref} modelId="model-1" />);
    await waitFor(() => expect(screen.getByPlaceholderText('Model name in slicer...')).toBeInTheDocument());

    addAlias('Pending alias', method);
    expect(screen.getByText(/Failed to add alias:.*no cryptographically secure random source available/)).toBeInTheDocument();
    expect(screen.getByPlaceholderText('Model name in slicer...')).toHaveValue('Pending alias');
    expect(ref.current!.hasChanges()).toBe(false);
    expect(updateAliases).not.toHaveBeenCalled();

    vi.stubGlobal('crypto', secureCrypto);
    fireEvent.click(screen.getByTitle('Add alias'));
    expect(screen.getByText('Pending alias')).toBeInTheDocument();
    expect(ref.current!.hasChanges()).toBe(true);
    expect(crypto.getRandomValues).toHaveBeenCalledTimes(1);
  });
});
