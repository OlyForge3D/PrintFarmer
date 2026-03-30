import { useState, useCallback } from 'react';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Button, Radio, FileUpload } from '@/common/components/ui';
import { DatabaseIcon, DownloadIcon, UploadIcon, RefreshIcon, CheckCircleIcon, AlertCircleIcon, CloudDownloadIcon } from '@/common/components/icons/MdiIcons';
import { 
  exportCatalog, 
  exportPrinters, 
  exportFull, 
  importCatalog, 
  importFull, 
  reloadSeed, 
  downloadAsJson, 
  generateExportFilename,
  getCatalogVersion,
  checkCatalogUpdates,
  applyCatalogUpdates,
} from '@/services/adminDataService';
import { ImportMode, type ImportResponseDto, type ExportHistoryItem, type CatalogVersionDto, type CatalogUpdateCheckResult, type CatalogUpdateApplyResult } from '@/types/adminData';

export function DataManagementPage() {
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [success, setSuccess] = useState<string | null>(null);
  const [importResult, setImportResult] = useState<ImportResponseDto | null>(null);
  const [importMode, setImportMode] = useState<ImportMode>(ImportMode.Merge);
  const [exportHistory, setExportHistory] = useState<ExportHistoryItem[]>([]);

  // Catalog update state
  const [catalogVersion, setCatalogVersion] = useState<CatalogVersionDto | null>(null);
  const [updateCheck, setUpdateCheck] = useState<CatalogUpdateCheckResult | null>(null);
  const [updateResult, setUpdateResult] = useState<CatalogUpdateApplyResult | null>(null);
  const [catalogLoading, setCatalogLoading] = useState(false);

  const addToHistory = useCallback((type: 'catalog' | 'printers' | 'full', filename: string) => {
    const item: ExportHistoryItem = {
      timestamp: new Date().toISOString(),
      type,
      filename,
    };
    setExportHistory(prev => [item, ...prev].slice(0, 10)); // Keep last 10
  }, []);

  const handleExportCatalog = async () => {
    setLoading(true);
    setError(null);
    setSuccess(null);
    try {
      const data = await exportCatalog();
      const filename = generateExportFilename('catalog');
      downloadAsJson(data, filename);
      addToHistory('catalog', filename);
      setSuccess(`Catalog exported successfully as ${filename}`);
    } catch (err) {
      setError(`Failed to export catalog: ${err instanceof Error ? err.message : 'Unknown error'}`);
    } finally {
      setLoading(false);
    }
  };

  const handleExportPrinters = async () => {
    setLoading(true);
    setError(null);
    setSuccess(null);
    try {
      const data = await exportPrinters();
      const filename = generateExportFilename('printers');
      downloadAsJson(data, filename);
      addToHistory('printers', filename);
      setSuccess(`Printers exported successfully as ${filename}`);
    } catch (err) {
      setError(`Failed to export printers: ${err instanceof Error ? err.message : 'Unknown error'}`);
    } finally {
      setLoading(false);
    }
  };

  const handleExportFull = async () => {
    setLoading(true);
    setError(null);
    setSuccess(null);
    try {
      const data = await exportFull();
      const filename = generateExportFilename('full');
      downloadAsJson(data, filename);
      addToHistory('full', filename);
      setSuccess(`Full backup exported successfully as ${filename}`);
    } catch (err) {
      setError(`Failed to export full backup: ${err instanceof Error ? err.message : 'Unknown error'}`);
    } finally {
      setLoading(false);
    }
  };

  const handleImportFile = async (files: FileList | null) => {
    const file = files?.[0];
    if (!file) return;

    setLoading(true);
    setError(null);
    setSuccess(null);
    setImportResult(null);

    try {
      const text = await file.text();
      const data = JSON.parse(text);

      // Determine if it's a full backup or catalog only
      const isFull = 'catalog' in data && 'printers' in data;
      
      let result: ImportResponseDto;
      if (isFull) {
        result = await importFull(data, importMode);
      } else {
        result = await importCatalog(data, importMode);
      }

      setImportResult(result);
      if (result.success) {
        setSuccess(`Import completed successfully! ${result.statistics.totalItemsImported} items imported.`);
      } else {
        setError(`Import completed with errors. See details below.`);
      }
    } catch (err) {
      if (err instanceof SyntaxError) {
        setError('Invalid JSON file. Please select a valid export file.');
      } else {
        setError(`Failed to import: ${err instanceof Error ? err.message : 'Unknown error'}`);
      }
    } finally {
      setLoading(false);
    }
  };

  const handleReloadSeed = async () => {
    setLoading(true);
    setError(null);
    setSuccess(null);
    setImportResult(null);
    try {
      const result = await reloadSeed();
      if (result.success) {
        setSuccess(result.message);
      } else {
        setError(result.message);
      }
    } catch (err) {
      setError(`Failed to reload seed data: ${err instanceof Error ? err.message : 'Unknown error'}`);
    } finally {
      setLoading(false);
    }
  };

  const handleCheckUpdates = async () => {
    setCatalogLoading(true);
    setError(null);
    setSuccess(null);
    setUpdateCheck(null);
    setUpdateResult(null);
    try {
      const [version, check] = await Promise.all([
        getCatalogVersion(),
        checkCatalogUpdates(),
      ]);
      setCatalogVersion(version);
      setUpdateCheck(check);
      if (check.error) {
        setError(`Update check failed: ${check.error}`);
      } else if (check.updateAvailable) {
        setSuccess(`Update available: ${check.changedFiles.length} file(s) changed.`);
      } else {
        setSuccess('Catalog is up to date.');
      }
    } catch (err) {
      setError(`Failed to check for updates: ${err instanceof Error ? err.message : 'Unknown error'}`);
    } finally {
      setCatalogLoading(false);
    }
  };

  const handleApplyUpdates = async () => {
    setCatalogLoading(true);
    setError(null);
    setSuccess(null);
    try {
      const result = await applyCatalogUpdates();
      setUpdateResult(result);
      if (result.success) {
        setSuccess(`Catalog updated to ${result.appliedVersion}. Categories updated: ${result.updatedCategories.join(', ')}.`);
        setUpdateCheck(null);
        setCatalogVersion({
          version: result.appliedVersion,
          appliedAt: result.appliedAt,
          source: 'remote',
        });
      } else {
        setError(`Update failed: ${result.error ?? 'Unknown error'}`);
      }
    } catch (err) {
      setError(`Failed to apply updates: ${err instanceof Error ? err.message : 'Unknown error'}`);
    } finally {
      setCatalogLoading(false);
    }
  };

  return (
    <PageTemplate
      title="Data Management"
      subtitle="Export, import, and manage PrintFarmer data"
      icon={DatabaseIcon}
    >
      <div className="space-y-6">
        {/* Status Messages */}
        {error && (
          <div className="bg-pf-error/10 border border-pf-error/30 rounded-lg p-4">
            <div className="flex items-start">
              <AlertCircleIcon className="w-5 h-5 text-pf-error mt-0.5 mr-3 shrink-0" />
              <div className="flex-1">
                <h3 className="text-sm font-medium text-pf-error">Error</h3>
                <p className="text-sm text-pf-error mt-1">{error}</p>
              </div>
            </div>
          </div>
        )}

        {success && (
          <div className="bg-pf-success/10 border border-pf-success/30 rounded-lg p-4">
            <div className="flex items-start">
              <CheckCircleIcon className="w-5 h-5 text-pf-success mt-0.5 mr-3 shrink-0" />
              <div className="flex-1">
                <h3 className="text-sm font-medium text-pf-success">Success</h3>
                <p className="text-sm text-pf-success mt-1">{success}</p>
              </div>
            </div>
          </div>
        )}

        {/* Export Section */}
        <div className="bg-pf-bg-1 rounded-lg p-6 border border-pf-border">
          <h2 className="text-lg font-semibold text-pf-text-primary mb-4 flex items-center">
            <DownloadIcon className="w-5 h-5 mr-2" />
            Export Data
          </h2>
          <p className="text-sm text-pf-text-secondary mb-4">
            Export PrintFarmer data as JSON files for backup or sharing.
          </p>
          <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
            <Button
              onClick={handleExportCatalog}
              disabled={loading}
              variant="secondary"
              className="w-full"
              iconLeft={<DownloadIcon className="w-4 h-4" />}
            >
              Export Catalog
            </Button>
            <Button
              onClick={handleExportPrinters}
              disabled={loading}
              variant="secondary"
              className="w-full"
              iconLeft={<DownloadIcon className="w-4 h-4" />}
            >
              Export Printers
            </Button>
            <Button
              onClick={handleExportFull}
              disabled={loading}
              variant="primary"
              className="w-full"
              iconLeft={<DownloadIcon className="w-4 h-4" />}
            >
              Export Full Backup
            </Button>
          </div>
          <div className="mt-4 text-xs text-pf-text-secondary">
            <ul className="list-disc list-inside space-y-1">
              <li><strong>Catalog:</strong> Manufacturers, printer models, and component definitions</li>
              <li><strong>Printers:</strong> Configured printer instances only</li>
              <li><strong>Full Backup:</strong> Everything (catalog + printers + locations)</li>
            </ul>
          </div>
        </div>

        {/* Import Section */}
        <div className="bg-pf-bg-1 rounded-lg p-6 border border-pf-border">
          <h2 className="text-lg font-semibold text-pf-text-primary mb-4 flex items-center">
            <UploadIcon className="w-5 h-5 mr-2" />
            Import Data
          </h2>
          <p className="text-sm text-pf-text-secondary mb-4">
            Import data from a JSON export file. Choose import mode carefully.
          </p>

          {/* Import Mode Selection */}
          <div className="mb-4">
            <label className="block text-sm font-medium text-pf-text-primary mb-2">
              Import Mode
            </label>
            <div className="space-y-2">
              <label className="flex items-start cursor-pointer">
                <Radio
                  name="importMode"
                  value={ImportMode.Merge.toString()}
                  checked={importMode === ImportMode.Merge}
                  onChange={(e) => setImportMode(Number(e.target.value))}
                  className="mt-1 mr-3"
                />
                <div>
                  <div className="text-sm font-medium text-pf-text-primary">
                    Merge (Recommended)
                  </div>
                  <div className="text-xs text-pf-text-secondary">
                    Adds new items, skips duplicates. Safe for production use.
                  </div>
                </div>
              </label>
              <label className="flex items-start cursor-pointer">
                <Radio
                  name="importMode"
                  value={ImportMode.Replace.toString()}
                  checked={importMode === ImportMode.Replace}
                  onChange={(e) => setImportMode(Number(e.target.value))}
                  className="mt-1 mr-3"
                />
                <div>
                  <div className="text-sm font-medium text-pf-text-primary">
                    Replace
                  </div>
                  <div className="text-xs text-pf-error">
                    ⚠️ WARNING: Deletes ALL existing data before import. Use for factory reset only.
                  </div>
                </div>
              </label>
            </div>
          </div>

          {/* File Upload */}
          <div className="mb-4">
            <label htmlFor="import-file" className="block text-sm font-medium text-pf-text-primary mb-2">
              Select Import File
            </label>
            <FileUpload
              id="import-file"
              accept=".json"
              onChange={handleImportFile}
              disabled={loading}
              className="block w-full"
            />
            <p className="text-xs text-pf-text-secondary mt-2">
              Accepts JSON files exported from PrintFarmer (catalog or full backup)
            </p>
          </div>

          {/* Import Results */}
          {importResult && (
            <div className="mt-4 p-4 bg-pf-bg-2 rounded-sm border border-pf-border">
              <h3 className="text-sm font-semibold text-pf-text-primary mb-2">Import Results</h3>
              <div className="grid grid-cols-2 md:grid-cols-3 gap-2 text-xs">
                <div>
                  <span className="text-pf-text-secondary">Manufacturers:</span>{' '}
                  <span className="font-semibold text-pf-text-primary">{importResult.statistics.manufacturersImported}</span>
                </div>
                <div>
                  <span className="text-pf-text-secondary">Filament Types:</span>{' '}
                  <span className="font-semibold text-pf-text-primary">{importResult.statistics.filamentTypesImported}</span>
                </div>
                <div>
                  <span className="text-pf-text-secondary">Printer Models:</span>{' '}
                  <span className="font-semibold text-pf-text-primary">{importResult.statistics.printerModelsImported}</span>
                </div>
                <div>
                  <span className="text-pf-text-secondary">Hotends:</span>{' '}
                  <span className="font-semibold text-pf-text-primary">{importResult.statistics.hotendsImported}</span>
                </div>
                <div>
                  <span className="text-pf-text-secondary">Extruders:</span>{' '}
                  <span className="font-semibold text-pf-text-primary">{importResult.statistics.extrudersImported}</span>
                </div>
                <div>
                  <span className="text-pf-text-secondary">Toolheads:</span>{' '}
                  <span className="font-semibold text-pf-text-primary">{importResult.statistics.toolheadsImported}</span>
                </div>
                <div>
                  <span className="text-pf-text-secondary">Nozzles:</span>{' '}
                  <span className="font-semibold text-pf-text-primary">{importResult.statistics.nozzlesImported}</span>
                </div>
                <div>
                  <span className="text-pf-text-secondary">Printers:</span>{' '}
                  <span className="font-semibold text-pf-text-primary">{importResult.statistics.printersImported}</span>
                </div>
                <div>
                  <span className="text-pf-text-secondary">Duration:</span>{' '}
                  <span className="font-semibold text-pf-text-primary">{importResult.statistics.duration}</span>
                </div>
              </div>
              <div className="mt-2 text-sm font-semibold text-pf-text-primary">
                Total: {importResult.statistics.totalItemsImported} items
              </div>
              
              {importResult.warnings.length > 0 && (
                <div className="mt-3">
                  <h4 className="text-xs font-semibold text-pf-warning mb-1">Warnings:</h4>
                  <ul className="text-xs text-pf-warning list-disc list-inside">
                    {importResult.warnings.map((warning, idx) => (
                      <li key={idx}>{warning}</li>
                    ))}
                  </ul>
                </div>
              )}
              
              {importResult.errors.length > 0 && (
                <div className="mt-3">
                  <h4 className="text-xs font-semibold text-pf-error mb-1">Errors:</h4>
                  <ul className="text-xs text-pf-error list-disc list-inside">
                    {importResult.errors.map((err, idx) => (
                      <li key={idx}>{err}</li>
                    ))}
                  </ul>
                </div>
              )}
            </div>
          )}
        </div>

        {/* Seed Data Section */}
        <div className="bg-pf-bg-1 rounded-lg p-6 border border-pf-border">
          <h2 className="text-lg font-semibold text-pf-text-primary mb-4 flex items-center">
            <RefreshIcon className="w-5 h-5 mr-2" />
            Seed Data
          </h2>
          <p className="text-sm text-pf-text-secondary mb-4">
            Reload default seed data from YAML files. This adds missing manufacturers, printer models, and components.
          </p>
          <Button
            onClick={handleReloadSeed}
            disabled={loading}
            variant="secondary"
            iconLeft={<RefreshIcon className="w-4 h-4" />}
          >
            Reload Seed Data
          </Button>
          <div className="mt-4 text-xs text-pf-text-secondary">
            <p>
              <strong>Note:</strong> This operation uses merge mode and will not overwrite existing data. 
              Seed data is stored in <code className="bg-pf-bg-2 px-1 py-0.5 rounded-sm">data/seed/</code> YAML files.
            </p>
          </div>
        </div>

        {/* Catalog Updates Section */}
        <div className="bg-pf-bg-1 rounded-lg p-6 border border-pf-border">
          <h2 className="text-lg font-semibold text-pf-text-primary mb-4 flex items-center">
            <CloudDownloadIcon className="w-5 h-5 mr-2" />
            Catalog Updates
          </h2>
          <p className="text-sm text-pf-text-secondary mb-4">
            Check for and apply catalog data updates from the PrintFarmer repository. Updates include new manufacturers, printer models, and components.
          </p>

          {/* Current Version */}
          {catalogVersion && (
            <div className="mb-4 p-3 bg-pf-bg-2 rounded-sm border border-pf-border text-sm">
              <span className="text-pf-text-secondary">Current version:</span>{' '}
              <span className="font-semibold text-pf-text-primary">
                {catalogVersion.version ?? 'Not tracked'}
              </span>
              {catalogVersion.appliedAt && (
                <>
                  <span className="text-pf-text-secondary ml-4">Applied:</span>{' '}
                  <span className="text-pf-text-primary">
                    {new Date(catalogVersion.appliedAt).toLocaleString()}
                  </span>
                </>
              )}
            </div>
          )}

          {/* Action Buttons */}
          <div className="flex gap-3">
            <Button
              onClick={handleCheckUpdates}
              disabled={loading || catalogLoading}
              variant="secondary"
              iconLeft={<RefreshIcon className="w-4 h-4" />}
            >
              Check for Updates
            </Button>
            {updateCheck?.updateAvailable && !updateResult?.success && (
              <Button
                onClick={handleApplyUpdates}
                disabled={loading || catalogLoading}
                variant="primary"
                iconLeft={<CloudDownloadIcon className="w-4 h-4" />}
              >
                Apply Updates
              </Button>
            )}
          </div>

          {/* Changed Files List */}
          {updateCheck?.updateAvailable && updateCheck.changedFiles.length > 0 && !updateResult?.success && (
            <div className="mt-4 p-4 bg-pf-bg-2 rounded-sm border border-pf-border">
              <h3 className="text-sm font-semibold text-pf-text-primary mb-2">
                Changed Files ({updateCheck.changedFiles.length})
              </h3>
              <div className="space-y-1 max-h-48 overflow-y-auto">
                {updateCheck.changedFiles.map((file, idx) => (
                  <div key={idx} className="flex items-center text-xs">
                    <span className={`inline-block w-16 px-1.5 py-0.5 rounded-sm text-center font-medium mr-2 ${
                      file.changeType === 'Added'
                        ? 'bg-pf-success/20 text-pf-success'
                        : file.changeType === 'Removed'
                          ? 'bg-pf-error/20 text-pf-error'
                          : 'bg-pf-warning/20 text-pf-warning'
                    }`}>
                      {file.changeType}
                    </span>
                    <span className="text-pf-text-secondary mr-2">[{file.category}]</span>
                    <span className="text-pf-text-primary font-mono">{file.fileName}</span>
                  </div>
                ))}
              </div>
            </div>
          )}

          {/* Apply Result */}
          {updateResult?.success && (
            <div className="mt-4 p-4 bg-pf-success/10 border border-pf-success/30 rounded-sm">
              <div className="flex items-center mb-2">
                <CheckCircleIcon className="w-4 h-4 text-pf-success mr-2" />
                <h3 className="text-sm font-semibold text-pf-success">Update Applied</h3>
              </div>
              <div className="text-xs text-pf-text-secondary space-y-1">
                <p>Version: <span className="font-semibold text-pf-text-primary">{updateResult.previousVersion ?? 'none'}</span> → <span className="font-semibold text-pf-text-primary">{updateResult.appliedVersion}</span></p>
                <p>Categories updated: <span className="font-semibold text-pf-text-primary">{updateResult.updatedCategories.join(', ')}</span></p>
              </div>
            </div>
          )}

          <div className="mt-4 text-xs text-pf-text-secondary">
            <p>
              <strong>Note:</strong> Updates are fetched from the PrintFarmer GitHub repository and applied using merge mode. Existing custom data is preserved.
            </p>
          </div>
        </div>

        {/* Export History */}
        {exportHistory.length > 0 && (
          <div className="bg-pf-bg-1 rounded-lg p-6 border border-pf-border">
            <h2 className="text-lg font-semibold text-pf-text-primary mb-4">
              Recent Exports
            </h2>
            <div className="space-y-2">
              {exportHistory.map((item, idx) => (
                <div 
                  key={idx} 
                  className="flex items-center justify-between text-sm p-2 bg-pf-bg-2 rounded-sm"
                >
                  <div>
                    <span className="font-medium text-pf-text-primary">{item.filename}</span>
                    <span className="text-pf-text-secondary ml-2">
                      ({item.type})
                    </span>
                  </div>
                  <span className="text-xs text-pf-text-secondary">
                    {new Date(item.timestamp).toLocaleString()}
                  </span>
                </div>
              ))}
            </div>
          </div>
        )}
      </div>
    </PageTemplate>
  );
}
