import React, { useState } from 'react';
import type { HarvestOptions } from '../HarvestWizard';

interface HarvestWizardStep2OptionsProps {
  options: HarvestOptions;
  onComplete: (options: HarvestOptions) => void;
}

export function HarvestWizardStep2Options({
  options,
  onComplete,
}: HarvestWizardStep2OptionsProps) {
  const [localOptions, setLocalOptions] = useState<HarvestOptions>(options);
  const [errors, setErrors] = useState<Record<string, string>>({});

  const handleMaxFileSizeChange = (value: string) => {
    const num = parseInt(value, 10);
    if (!isNaN(num) && num > 0) {
      setLocalOptions((prev: HarvestOptions) => ({
        ...prev,
        maxFileSizeBytes: num * 1024 * 1024, // Convert MB to bytes
      }));
      setErrors((prev: Record<string, string>) => {
        const newErrors = { ...prev };
        delete newErrors.maxFileSize;
        return newErrors;
      });
    }
  };

  const handleMinFileSizeChange = (value: string) => {
    const num = parseInt(value, 10);
    if (!isNaN(num) && num >= 0) {
      setLocalOptions((prev: HarvestOptions) => ({
        ...prev,
        minFileSizeBytes: num * 1024, // Convert KB to bytes
      }));
      setErrors((prev: Record<string, string>) => {
        const newErrors = { ...prev };
        delete newErrors.minFileSize;
        return newErrors;
      });
    }
  };

  const handleFileExtensionsChange = (value: string) => {
    setLocalOptions((prev: HarvestOptions) => ({
      ...prev,
      fileExtensions: value.split(',').map(ext => ext.trim()),
    }));
  };

  const handleIncludeSubdirectoriesChange = (checked: boolean) => {
    setLocalOptions((prev: HarvestOptions) => ({
      ...prev,
      includeSubdirectories: checked,
    }));
  };

  const handleDuplicateHandlingChange = (value: string) => {
    setLocalOptions((prev: HarvestOptions) => ({
      ...prev,
      duplicateHandling: value as 'skip' | 'replace' | 'keep',
    }));
  };

  const validateAndSubmit = () => {
    const newErrors: Record<string, string> = {};

    if (localOptions.maxFileSizeBytes <= 0) {
      newErrors.maxFileSize = 'Maximum file size must be greater than 0';
    }

    if (localOptions.minFileSizeBytes < 0) {
      newErrors.minFileSize = 'Minimum file size cannot be negative';
    }

    if (
      localOptions.minFileSizeBytes &&
      localOptions.maxFileSizeBytes &&
      localOptions.minFileSizeBytes > localOptions.maxFileSizeBytes
    ) {
      newErrors.fileSize = 'Minimum size cannot be greater than maximum size';
    }

    if (!localOptions.fileExtensions || localOptions.fileExtensions.length === 0) {
      newErrors.fileExtensions = 'At least one file extension is required';
    }

    setErrors(newErrors);

    if (Object.keys(newErrors).length === 0) {
      onComplete(localOptions);
    }
  };

  const maxFileSizeMB = localOptions.maxFileSizeBytes / (1024 * 1024);
  const minFileSizeKB = localOptions.minFileSizeBytes / 1024;
  const extensionsDisplay = localOptions.fileExtensions.join(', ');

  return (
    <div className="space-y-6">
      <div>
        <label htmlFor="fileExtensions" className="block text-sm font-medium text-pf-text-primary mb-2">
          File Extensions
        </label>
        <input
          id="fileExtensions"
          type="text"
          value={extensionsDisplay}
          onChange={e => handleFileExtensionsChange(e.target.value)}
          placeholder=".gcode,.gco,.g"
          title="File extensions to harvest (comma-separated)"
          className="w-full px-3 py-2 border border-pf-border rounded-lg text-pf-text-primary bg-pf-surface placeholder-pf-text-secondary focus:border-pf-accent focus:outline-none"
        />
        <p className="text-xs text-pf-text-secondary mt-1">
          Comma-separated list of file extensions to harvest (e.g., .gcode,.gco,.g)
        </p>
        {errors.fileExtensions && (
          <p className="text-xs text-pf-error mt-1">{errors.fileExtensions}</p>
        )}
      </div>

      <div className="grid grid-cols-2 gap-4">
        <div>
          <label htmlFor="maxFileSize" className="block text-sm font-medium text-pf-text-primary mb-2">
            Maximum File Size (MB)
          </label>
          <input
            id="maxFileSize"
            type="number"
            value={maxFileSizeMB}
            onChange={e => handleMaxFileSizeChange(e.target.value)}
            min="1"
            title="Maximum file size in megabytes"
            className="w-full px-3 py-2 border border-pf-border rounded-lg text-pf-text-primary bg-pf-surface focus:border-pf-accent focus:outline-none"
          />
          {errors.maxFileSize && (
            <p className="text-xs text-pf-error mt-1">{errors.maxFileSize}</p>
          )}
        </div>

        <div>
          <label htmlFor="minFileSize" className="block text-sm font-medium text-pf-text-primary mb-2">
            Minimum File Size (KB)
          </label>
          <input
            id="minFileSize"
            type="number"
            value={minFileSizeKB}
            onChange={e => handleMinFileSizeChange(e.target.value)}
            min="0"
            title="Minimum file size in kilobytes"
            className="w-full px-3 py-2 border border-pf-border rounded-lg text-pf-text-primary bg-pf-surface focus:border-pf-accent focus:outline-none"
          />
          {errors.minFileSize && (
            <p className="text-xs text-pf-error mt-1">{errors.minFileSize}</p>
          )}
        </div>
      </div>

      {errors.fileSize && (
        <div className="p-3 bg-pf-error-bg rounded-lg">
          <p className="text-sm text-pf-error">{errors.fileSize}</p>
        </div>
      )}

      <div className="space-y-3">
        <label htmlFor="includeSubdirs" className="flex items-center gap-3 cursor-pointer">
          <input
            id="includeSubdirs"
            type="checkbox"
            checked={localOptions.includeSubdirectories}
            onChange={e => handleIncludeSubdirectoriesChange(e.target.checked)}
            className="rounded border-pf-border"
            title="Include files in subdirectories"
          />
          <span className="text-sm text-pf-text-primary">Include subdirectories</span>
        </label>
      </div>

      <div>
        <label htmlFor="duplicateHandling" className="block text-sm font-medium text-pf-text-primary mb-2">
          Duplicate Handling
        </label>
        <select
          id="duplicateHandling"
          value={localOptions.duplicateHandling}
          onChange={e =>
            handleDuplicateHandlingChange(e.target.value)
          }
          title="How to handle files that already exist"
          className="w-full px-3 py-2 border border-pf-border rounded-lg text-pf-text-primary bg-pf-surface focus:border-pf-accent focus:outline-none"
        >
          <option value="skip">Skip duplicates</option>
          <option value="replace">Replace existing files</option>
          <option value="keep">Keep both (rename new)</option>
        </select>
        <p className="text-xs text-pf-text-secondary mt-1">
          Choose how to handle files that already exist in the library
        </p>
      </div>

      <div className="bg-pf-info-bg border border-pf-info rounded-lg p-3">
        <p className="text-xs text-pf-info font-semibold">Configuration Summary</p>
        <ul className="text-xs text-pf-info mt-2 space-y-1">
          <li>• File extensions: {extensionsDisplay}</li>
          <li>• Size range: {minFileSizeKB} KB to {maxFileSizeMB} MB</li>
          <li>• Subdirectories: {localOptions.includeSubdirectories ? 'Included' : 'Excluded'}</li>
          <li>• Duplicates: {localOptions.duplicateHandling}</li>
        </ul>
      </div>

      <button
        onClick={validateAndSubmit}
        className="w-full px-4 py-2 bg-pf-accent text-white rounded-lg hover:bg-pf-accent-hover transition-colors font-medium"
      >
        Continue with These Settings
      </button>
    </div>
  );
}
