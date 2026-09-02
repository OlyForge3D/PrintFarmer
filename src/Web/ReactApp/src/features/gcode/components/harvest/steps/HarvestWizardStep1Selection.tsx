/* eslint-disable local/pf-no-raw-html-controls */
import React, { useState, useMemo } from 'react';
import { Printer, PrinterBackend, GcodeHarvestOperation } from '@/types/api';
import { toPrinterBackend } from '@/common/utils/enumHelpers';
import { formatPrinterModelSubtitle } from '@/common/utils/printerModelDisplay';
// No MdiIcons used in this component
import { CloseIcon, CheckCircleIcon } from '@/common/components/icons/MdiIcons';
import { Input } from '@/common/components/ui/Input';
import { Select } from '@/common/components/ui/Select';
import { Button } from '@/common/components/ui/Button';

interface HarvestWizardStep1SelectionProps {
  printers: Printer[];
  selectedPrinterId: string | null;
  onSelect: (printerId: string) => void;
  activeHarvests?: GcodeHarvestOperation[];
  /** True while activeHarvests is still loading — printer selection is
   *  disabled until it resolves so a user cannot start a harvest that
   *  conflicts with one already running. */
  isLoadingActiveHarvests?: boolean;
}

// Helper to convert PrinterBackend enum (number or string) to display string
// API returns string values due to JsonStringEnumConverter
function backendToString(backend: PrinterBackend | string | undefined): string {
  if (backend === undefined || backend === null) return 'Unknown';
  
  // If it's already a string (from API), return it directly if valid
  if (typeof backend === 'string') {
    const validBackends = ['Moonraker', 'PrusaLink', 'SDCP', 'OctoPrint', 'FlashForge'];
    return validBackends.includes(backend) ? backend : 'Unknown';
  }
  
  // If it's a number (enum value), map it
  const backendNames: Record<PrinterBackend, string> = {
    [PrinterBackend.Unknown]: 'Unknown',
    [PrinterBackend.Moonraker]: 'Moonraker',
    [PrinterBackend.PrusaLink]: 'PrusaLink',
    [PrinterBackend.SDCP]: 'SDCP',
    [PrinterBackend.OctoPrint]: 'OctoPrint',
    [PrinterBackend.FlashForge]: 'FlashForge',
  };
  return backendNames[backend] || 'Unknown';
}

export function HarvestWizardStep1Selection({
  printers,
  selectedPrinterId,
  onSelect,
  activeHarvests = [],
  isLoadingActiveHarvests = false,
}: HarvestWizardStep1SelectionProps) {
  // Filter state
  const [searchQuery, setSearchQuery] = useState('');
  const [selectedBackend, setSelectedBackend] = useState<PrinterBackend | undefined>(undefined);
  const [selectedModel, setSelectedModel] = useState<string>('');

  // Build a set of printer IDs that have active harvests
  const printersWithActiveHarvests = useMemo(() => {
    return new Set(activeHarvests.map(h => h.printerId));
  }, [activeHarvests]);

  // Get unique values for filter options - only online printers
  const onlinePrinters = useMemo(() => printers.filter(p => p.isOnline), [printers]);

  const uniqueBackends = useMemo(() => {
    const backends = new Set(
      onlinePrinters
        .map(p => p.backend)
        .filter((b): b is PrinterBackend => b !== undefined)
    );
    return Array.from(backends).sort((a, b) => String(a).localeCompare(String(b)));
  }, [onlinePrinters]);

  const uniqueModels = useMemo(() => {
    const models = new Set(
      onlinePrinters
        .map(p => formatPrinterModelSubtitle(p.manufacturerName, p.modelName))
        .filter(Boolean)
    );
    return Array.from(models).sort();
  }, [onlinePrinters]);

  // Apply filters
  const filteredPrinters = useMemo(() => {
    return onlinePrinters.filter(p => {
      // Backend filter
      if (selectedBackend !== undefined && p.backend !== selectedBackend) {
        return false;
      }

      // Model filter
      if (selectedModel) {
        const printerModel = formatPrinterModelSubtitle(p.manufacturerName, p.modelName);
        if (printerModel !== selectedModel) {
          return false;
        }
      }

      // Search query filter (searches in name, model, backend, server URL)
      if (searchQuery.trim()) {
        const query = searchQuery.toLowerCase();
        const searchableText = `${p.name} ${p.modelName} ${backendToString(p.backend)} ${p.backendUrl}`.toLowerCase();
        return searchableText.includes(query);
      }

      return true;
    });
  }, [onlinePrinters, searchQuery, selectedBackend, selectedModel]);

  const clearFilters = () => {
    setSearchQuery('');
    setSelectedBackend(undefined);
    setSelectedModel('');
  };

  const hasActiveFilters = searchQuery.trim() !== '' || selectedBackend !== undefined || selectedModel !== '';

  if (filteredPrinters.length === 0) {
    return (
      <div className="space-y-6">
        {/* Search and Filters Section - Single Row */}
        <div className="border border-pf-border rounded-lg p-4 bg-pf-bg-1">
          <div className="flex flex-col gap-2 mb-3">
            <label className="text-xs font-medium text-pf-text-secondary uppercase tracking-wide">
              Filter Printers
            </label>
          </div>
          <div className="flex gap-3 items-end">
            {/* Search Box */}
            <div className="flex-1 min-w-0">
              <label className="text-xs font-medium text-pf-text-secondary block mb-1">
                Search Printers
              </label>
              <Input
                type="text"
                placeholder="Name, model, backend..."
                value={searchQuery}
                onChange={e => setSearchQuery(e.target.value)}
                className="w-full"
              />
            </div>

            {/* Backend Dropdown */}
            {uniqueBackends.length > 0 && (
              <div className="w-40">
                <label htmlFor="backend-filter" className="text-xs font-medium text-pf-text-secondary block mb-1">
                  Backend
                </label>
                <Select
                  id="backend-filter"
                  value={selectedBackend ?? ''}
                  onChange={e => setSelectedBackend(e.target.value ? toPrinterBackend(e.target.value) : undefined)}
                  className="w-full"
                >
                  <option value="">All backends</option>
                  {uniqueBackends.map(backend => (
                    <option key={backend} value={backend}>
                      {backendToString(backend)}
                    </option>
                  ))}
                </Select>
              </div>
            )}

            {/* Model Dropdown */}
            {uniqueModels.length > 0 && (
              <div className="w-56">
                <label htmlFor="model-filter" className="text-xs font-medium text-pf-text-secondary block mb-1">
                  Make - Model
                </label>
                <Select
                  id="model-filter"
                  value={selectedModel}
                  onChange={e => setSelectedModel(e.target.value)}
                  className="w-full"
                >
                  <option value="">All models</option>
                  {uniqueModels.map(model => (
                    <option key={model} value={model}>
                      {model}
                    </option>
                  ))}
                </Select>
              </div>
            )}

            {/* Clear Filters Button */}
            {hasActiveFilters && (
              <Button
                variant="secondary"
                size="sm"
                onClick={clearFilters}
                iconLeft={<CloseIcon className="w-4 h-4" />}
              >
                Clear
              </Button>
            )}
          </div>
        </div>

        {/* No Results Message */}
        <div className="text-center py-12">
          <svg
            className="w-16 h-16 text-pf-warning mx-auto mb-4"
            fill="none"
            viewBox="0 0 24 24"
            stroke="currentColor"
          >
            <path
              strokeLinecap="round"
              strokeLinejoin="round"
              strokeWidth={2}
              d="M12 8v4m0 4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z"
            />
          </svg>
          <p className="text-pf-text-secondary text-lg font-medium">
            No online printers match your filters
          </p>
          <p className="text-pf-text-secondary text-sm mt-2">
            Try adjusting your search criteria or filters.
          </p>
        </div>
      </div>
    );
  }

  return (
    <div className="space-y-4">
      {/* Loading banner - active harvests are still being fetched, so printer
          selection is disabled to avoid starting a conflicting harvest */}
      {isLoadingActiveHarvests && (
        <div
          role="status"
          aria-live="polite"
          className="flex items-center gap-2 rounded-lg border border-pf-border bg-pf-bg-1 px-4 py-2 text-sm text-pf-text-secondary"
        >
          <svg
            className="w-4 h-4 animate-spin text-pf-accent"
            fill="none"
            viewBox="0 0 24 24"
          >
            <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
            <path
              className="opacity-75"
              fill="currentColor"
              d="M4 12a8 8 0 018-8v4a4 4 0 00-4 4H4z"
            />
          </svg>
          Checking for active harvests…
        </div>
      )}

      {/* Filter Summary */}
      <div className="text-sm text-pf-text-secondary">
        Showing <span className="font-semibold text-pf-text-primary">{filteredPrinters.length}</span> of{' '}
        <span className="font-semibold text-pf-text-primary">{onlinePrinters.length}</span> online printers
      </div>

      {/* Search and Filters Section - Single Row */}
      <div className="border border-pf-border rounded-lg p-4 bg-pf-bg-1">
        <div className="flex gap-3 items-end">
          {/* Search Box */}
          <div className="flex-1 min-w-0">
            <label className="text-xs font-medium text-pf-text-secondary block mb-1">
              Search Printers
            </label>
            <Input
              type="text"
              placeholder="Name, model, backend..."
              value={searchQuery}
              onChange={e => setSearchQuery(e.target.value)}
              className="w-full"
            />
          </div>

          {/* Backend Dropdown */}
          {uniqueBackends.length > 0 && (
            <div className="w-40">
              <label className="text-xs font-medium text-pf-text-secondary block mb-1">
                Backend
              </label>
              <Select
                value={selectedBackend ?? ''}
                onChange={e => setSelectedBackend(e.target.value ? toPrinterBackend(e.target.value) : undefined)}
                className="w-full"
                title="Filter by backend type"
              >
                <option value="">All backends</option>
                {uniqueBackends.map(backend => (
                  <option key={backend} value={backend}>
                    {backendToString(backend)}
                  </option>
                ))}
              </Select>
            </div>
          )}

          {/* Model Dropdown */}
          {uniqueModels.length > 0 && (
            <div className="w-56">
              <label className="text-xs font-medium text-pf-text-secondary block mb-1">
                Make - Model
              </label>
              <Select
                value={selectedModel}
                onChange={e => setSelectedModel(e.target.value)}
                className="w-full"
                title="Filter by printer model"
              >
                <option value="">All models</option>
                {uniqueModels.map(model => (
                  <option key={model} value={model}>
                    {model}
                  </option>
                ))}
              </Select>
            </div>
          )}

          {/* Clear Filters Button */}
          {hasActiveFilters && (
            <Button
              variant="secondary"
              size="sm"
              onClick={clearFilters}
              iconLeft={<CloseIcon className="w-4 h-4" />}
              className="shrink-0"
            >
              Clear
            </Button>
          )}
        </div>
      </div>

      {/* Printer Selection Grid */}
      <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
        {filteredPrinters.map(printer => {
          const hasActiveHarvest = printersWithActiveHarvests.has(printer.id);
          const isDisabled = hasActiveHarvest || isLoadingActiveHarvests;
          
          return (
          <button
            key={printer.id}
            onClick={() => !isDisabled && onSelect(printer.id)}
            disabled={isDisabled}
            className={`p-4 rounded-lg border-2 text-left transition-all ${
              hasActiveHarvest
                ? 'border-pf-warning bg-pf-warning-bg cursor-not-allowed opacity-60'
                : isLoadingActiveHarvests
                ? 'border-pf-border bg-pf-bg-1 cursor-not-allowed opacity-60'
                : selectedPrinterId === printer.id
                ? 'border-pf-accent bg-pf-accent-bg'
                : 'border-pf-border hover:border-pf-accent hover:bg-pf-bg-2'
            }`}
            title={
              hasActiveHarvest
                ? 'This printer has an active harvest in progress'
                : isLoadingActiveHarvests
                ? 'Checking for active harvests…'
                : undefined
            }
          >
            <div className="flex items-start justify-between">
              <div className="flex-1">
                <div className="font-semibold text-pf-text-primary flex items-center gap-2">
                  {printer.name}
                  <span className="inline-block w-2 h-2 rounded-full bg-pf-success" />
                  {hasActiveHarvest && (
                    <span className="ml-auto text-xs bg-pf-warning text-[var(--pf-text-inverse)] px-2 py-1 rounded-sm whitespace-nowrap">
                      Harvest in progress
                    </span>
                  )}
                </div>
                <div className="text-sm text-pf-text-secondary mt-1">
                  {formatPrinterModelSubtitle(printer.manufacturerName, printer.modelName)}
                </div>
                <div className="text-xs text-pf-text-secondary mt-2 font-mono">
                  {backendToString(printer.backend)} • {printer.backendUrl}
                </div>
              </div>
              {selectedPrinterId === printer.id && !hasActiveHarvest && (
                <CheckCircleIcon className="w-5 h-5 text-pf-accent shrink-0" />
              )}
            </div>
          </button>
        );
        })}
      </div>
    </div>
  );
}
