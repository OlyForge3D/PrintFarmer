import React, { useState, useMemo } from 'react';
import { Printer, PrinterBackend, GcodeHarvestOperation } from '@/types/api';
import { CheckCircle, X } from 'lucide-react';

interface HarvestWizardStep1SelectionProps {
  printers: Printer[];
  selectedPrinterId: string | null;
  onSelect: (printerId: string) => void;
  activeHarvests?: GcodeHarvestOperation[];
}

// Helper to convert PrinterBackend enum to string
function backendToString(backend: PrinterBackend | undefined): string {
  if (backend === undefined) return 'Unknown';
  const backendNames: Record<PrinterBackend, string> = {
    [PrinterBackend.Moonraker]: 'Moonraker',
    [PrinterBackend.PrusaLink]: 'PrusaLink',
    [PrinterBackend.SDCP]: 'SDCP',
    [PrinterBackend.OctoPrint]: 'OctoPrint',
  };
  return backendNames[backend] || 'Unknown';
}

export function HarvestWizardStep1Selection({
  printers,
  selectedPrinterId,
  onSelect,
  activeHarvests = [],
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
    return Array.from(backends).sort((a, b) => a - b);
  }, [onlinePrinters]);

  const uniqueModels = useMemo(() => {
    const models = new Set(
      onlinePrinters
        .map(p => {
          const model = p.modelName || 'Unknown';
          const manufacturer = p.manufacturerName || 'Unknown';
          return `${manufacturer} - ${model}`;
        })
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
        const printerModel = `${p.manufacturerName || 'Unknown'} - ${p.modelName || 'Unknown'}`;
        if (printerModel !== selectedModel) {
          return false;
        }
      }

      // Search query filter (searches in name, model, backend, server URL)
      if (searchQuery.trim()) {
        const query = searchQuery.toLowerCase();
        const searchableText = `${p.name} ${p.modelName} ${backendToString(p.backend)} ${p.serverUrl}`.toLowerCase();
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
        <div className="border border-pf-border rounded-lg p-4 bg-pf-background-secondary">
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
              <input
                type="text"
                placeholder="Name, model, backend..."
                value={searchQuery}
                onChange={e => setSearchQuery(e.target.value)}
                className="w-full px-3 py-2 rounded-lg border border-pf-border bg-pf-background text-pf-text-primary placeholder-pf-text-secondary focus:outline-none focus:border-pf-accent text-sm"
              />
            </div>

            {/* Backend Dropdown */}
            {uniqueBackends.length > 0 && (
              <div className="w-40">
                <label htmlFor="backend-filter" className="text-xs font-medium text-pf-text-secondary block mb-1">
                  Backend
                </label>
                <select
                  id="backend-filter"
                  value={selectedBackend ?? ''}
                  onChange={e => setSelectedBackend(e.target.value ? parseInt(e.target.value) as PrinterBackend : undefined)}
                  className="w-full px-3 py-2 rounded-lg border border-pf-border bg-pf-background text-pf-text-primary focus:outline-none focus:border-pf-accent text-sm"
                >
                  <option value="">All backends</option>
                  {uniqueBackends.map(backend => (
                    <option key={backend} value={backend}>
                      {backendToString(backend)}
                    </option>
                  ))}
                </select>
              </div>
            )}

            {/* Model Dropdown */}
            {uniqueModels.length > 0 && (
              <div className="w-56">
                <label htmlFor="model-filter" className="text-xs font-medium text-pf-text-secondary block mb-1">
                  Make - Model
                </label>
                <select
                  id="model-filter"
                  value={selectedModel}
                  onChange={e => setSelectedModel(e.target.value)}
                  className="w-full px-3 py-2 rounded-lg border border-pf-border bg-pf-background text-pf-text-primary focus:outline-none focus:border-pf-accent text-sm"
                >
                  <option value="">All models</option>
                  {uniqueModels.map(model => (
                    <option key={model} value={model}>
                      {model}
                    </option>
                  ))}
                </select>
              </div>
            )}

            {/* Clear Filters Button */}
            {hasActiveFilters && (
              <button
                onClick={clearFilters}
                className="px-3 py-2 rounded-lg bg-pf-background text-pf-text-secondary border border-pf-border hover:bg-pf-hover text-sm transition-colors flex items-center gap-1"
              >
                <X className="w-4 h-4" />
                Clear
              </button>
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
      {/* Filter Summary */}
      <div className="text-sm text-pf-text-secondary">
        Showing <span className="font-semibold text-pf-text-primary">{filteredPrinters.length}</span> of{' '}
        <span className="font-semibold text-pf-text-primary">{onlinePrinters.length}</span> online printers
      </div>

      {/* Search and Filters Section - Single Row */}
      <div className="border border-pf-border rounded-lg p-4 bg-pf-background-secondary">
        <div className="flex gap-3 items-end">
          {/* Search Box */}
          <div className="flex-1 min-w-0">
            <label className="text-xs font-medium text-pf-text-secondary block mb-1">
              Search Printers
            </label>
            <input
              type="text"
              placeholder="Name, model, backend..."
              value={searchQuery}
              onChange={e => setSearchQuery(e.target.value)}
              className="w-full px-3 py-2 rounded-lg border border-pf-border bg-pf-background text-pf-text-primary placeholder-pf-text-secondary focus:outline-none focus:border-pf-accent text-sm"
            />
          </div>

          {/* Backend Dropdown */}
          {uniqueBackends.length > 0 && (
            <div className="w-40">
              <label className="text-xs font-medium text-pf-text-secondary block mb-1">
                Backend
              </label>
              <select
                value={selectedBackend ?? ''}
                onChange={e => setSelectedBackend(e.target.value ? parseInt(e.target.value) as PrinterBackend : undefined)}
                className="w-full px-3 py-2 rounded-lg border border-pf-border bg-pf-background text-pf-text-primary focus:outline-none focus:border-pf-accent text-sm"
                title="Filter by backend type"
              >
                <option value="">All backends</option>
                {uniqueBackends.map(backend => (
                  <option key={backend} value={backend}>
                    {backendToString(backend)}
                  </option>
                ))}
              </select>
            </div>
          )}

          {/* Model Dropdown */}
          {uniqueModels.length > 0 && (
            <div className="w-56">
              <label className="text-xs font-medium text-pf-text-secondary block mb-1">
                Make - Model
              </label>
              <select
                value={selectedModel}
                onChange={e => setSelectedModel(e.target.value)}
                className="w-full px-3 py-2 rounded-lg border border-pf-border bg-pf-background text-pf-text-primary focus:outline-none focus:border-pf-accent text-sm"
                title="Filter by printer model"
              >
                <option value="">All models</option>
                {uniqueModels.map(model => (
                  <option key={model} value={model}>
                    {model}
                  </option>
                ))}
              </select>
            </div>
          )}

          {/* Clear Filters Button */}
          {hasActiveFilters && (
            <button
              onClick={clearFilters}
              className="px-3 py-2 rounded-lg bg-pf-background text-pf-text-secondary border border-pf-border hover:bg-pf-hover text-sm transition-colors flex items-center gap-1 flex-shrink-0"
            >
              <X className="w-4 h-4" />
              Clear
            </button>
          )}
        </div>
      </div>

      {/* Printer Selection Grid */}
      <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
        {filteredPrinters.map(printer => {
          const hasActiveHarvest = printersWithActiveHarvests.has(printer.id);
          
          return (
          <button
            key={printer.id}
            onClick={() => !hasActiveHarvest && onSelect(printer.id)}
            disabled={hasActiveHarvest}
            className={`p-4 rounded-lg border-2 text-left transition-all ${
              hasActiveHarvest
                ? 'border-pf-warning bg-pf-warning-bg cursor-not-allowed opacity-60'
                : selectedPrinterId === printer.id
                ? 'border-pf-accent bg-pf-accent-bg'
                : 'border-pf-border hover:border-pf-accent hover:bg-pf-hover'
            }`}
            title={hasActiveHarvest ? 'This printer has an active harvest in progress' : undefined}
          >
            <div className="flex items-start justify-between">
              <div className="flex-1">
                <div className="font-semibold text-pf-text-primary flex items-center gap-2">
                  {printer.name}
                  <span className="inline-block w-2 h-2 rounded-full bg-pf-success" />
                  {hasActiveHarvest && (
                    <span className="ml-auto text-xs bg-pf-warning text-white px-2 py-1 rounded whitespace-nowrap">
                      Harvest in progress
                    </span>
                  )}
                </div>
                <div className="text-sm text-pf-text-secondary mt-1">
                  {printer.manufacturerName} {printer.modelName}
                </div>
                <div className="text-xs text-pf-text-secondary mt-2 font-mono">
                  {backendToString(printer.backend)} • {printer.serverUrl}
                </div>
              </div>
              {selectedPrinterId === printer.id && !hasActiveHarvest && (
                <CheckCircle className="w-5 h-5 text-pf-accent flex-shrink-0" />
              )}
            </div>
          </button>
        );
        })}
      </div>
    </div>
  );
}
