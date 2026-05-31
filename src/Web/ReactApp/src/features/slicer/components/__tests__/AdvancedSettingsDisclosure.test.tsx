import { describe, it, expect, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import '@testing-library/jest-dom';
import { AdvancedSettingsDisclosure } from '../../components/AdvancedSettingsDisclosure';

const STORAGE_KEY = 'pf.slicer.advancedDisclosure';

describe('AdvancedSettingsDisclosure', () => {
  beforeEach(() => {
    localStorage.clear();
  });

  it('renders collapsed by default', () => {
    render(
      <AdvancedSettingsDisclosure currentSettings={{}} originalSettings={{}}>
        <div data-testid="panel-content">Settings content</div>
      </AdvancedSettingsDisclosure>,
    );

    expect(screen.queryByTestId('panel-content')).not.toBeInTheDocument();
    expect(screen.getByText('Advanced Settings')).toBeInTheDocument();
  });

  it('expands when clicked and persists state to localStorage', () => {
    render(
      <AdvancedSettingsDisclosure currentSettings={{}} originalSettings={{}}>
        <div data-testid="panel-content">Settings content</div>
      </AdvancedSettingsDisclosure>,
    );

    const toggle = screen.getByRole('button', { name: /expand advanced settings/i });
    fireEvent.click(toggle);

    expect(screen.getByTestId('panel-content')).toBeInTheDocument();
    expect(localStorage.getItem(STORAGE_KEY)).toBe('true');
  });

  it('starts expanded when localStorage has true', () => {
    localStorage.setItem(STORAGE_KEY, 'true');

    render(
      <AdvancedSettingsDisclosure currentSettings={{}} originalSettings={{}}>
        <div data-testid="panel-content">Settings content</div>
      </AdvancedSettingsDisclosure>,
    );

    expect(screen.getByTestId('panel-content')).toBeInTheDocument();
  });

  it('collapses when clicked while expanded and persists', () => {
    localStorage.setItem(STORAGE_KEY, 'true');

    render(
      <AdvancedSettingsDisclosure currentSettings={{}} originalSettings={{}}>
        <div data-testid="panel-content">Settings content</div>
      </AdvancedSettingsDisclosure>,
    );

    const toggle = screen.getByRole('button', { name: /collapse advanced settings/i });
    fireEvent.click(toggle);

    expect(screen.queryByTestId('panel-content')).not.toBeInTheDocument();
    expect(localStorage.getItem(STORAGE_KEY)).toBe('false');
  });

  it('shows override count when settings differ from originals', () => {
    render(
      <AdvancedSettingsDisclosure
        currentSettings={{ layer_height: 0.3, infill: 30, speed: 100 }}
        originalSettings={{ layer_height: 0.2, infill: 20, speed: 100 }}
      >
        <div>Content</div>
      </AdvancedSettingsDisclosure>,
    );

    expect(screen.getByText('Advanced Settings (2 overrides)')).toBeInTheDocument();
  });

  it('shows singular override text for 1 override', () => {
    render(
      <AdvancedSettingsDisclosure
        currentSettings={{ layer_height: 0.3 }}
        originalSettings={{ layer_height: 0.2 }}
      >
        <div>Content</div>
      </AdvancedSettingsDisclosure>,
    );

    expect(screen.getByText('Advanced Settings (1 override)')).toBeInTheDocument();
  });

  it('shows no override count when all values match defaults', () => {
    render(
      <AdvancedSettingsDisclosure
        currentSettings={{ layer_height: 0.2, infill: 20 }}
        originalSettings={{ layer_height: 0.2, infill: 20 }}
      >
        <div>Content</div>
      </AdvancedSettingsDisclosure>,
    );

    expect(screen.getByText('Advanced Settings')).toBeInTheDocument();
    expect(screen.queryByText(/override/)).not.toBeInTheDocument();
  });

  it('ignores null/undefined values in current settings for override count', () => {
    render(
      <AdvancedSettingsDisclosure
        currentSettings={{ layer_height: null, infill: undefined, speed: 80 }}
        originalSettings={{ layer_height: 0.2, infill: 20, speed: 100 }}
      >
        <div>Content</div>
      </AdvancedSettingsDisclosure>,
    );

    // Only speed (80 vs 100) counts as override; null/undefined are skipped
    expect(screen.getByText('Advanced Settings (1 override)')).toBeInTheDocument();
  });
});
