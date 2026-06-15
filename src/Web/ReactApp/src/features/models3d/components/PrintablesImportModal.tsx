import { useEffect, useRef, useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { Modal } from '@/common/components/modals/Modal';
import { Button } from '@/common/components/ui/Button';
import { Input } from '@/common/components/ui/Input';
import { Spinner } from '@/common/components/ui/Spinner';
import { Alert } from '@/common/components/ui/Alert';
import { Checkbox } from '@/common/components/ui/Checkbox';
import { CubeIcon, ExternalLinkIcon } from '@/common/components/icons/MdiIcons';
import { apiClient } from '@/services/api';

interface PrintablesFileEntry {
  id: string;
  name: string;
  fileSize: number;
}

interface PrintablesPreview {
  modelId: string;
  name: string;
  creator: string;
  license: string | null;
  thumbnailUrl: string | null;
  sourceUrl: string;
  files: PrintablesFileEntry[];
}

interface PrintablesImportModalProps {
  isOpen: boolean;
  onClose: () => void;
  initialUrl?: string | null;
}

function formatFileSize(bytes: number): string {
  if (bytes === 0) return 'unknown size';
  const k = 1024;
  const sizes = ['B', 'KB', 'MB', 'GB'];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return `${(bytes / Math.pow(k, i)).toFixed(1)} ${sizes[i]}`;
}

const multiSelectThreshold = 2;

export function PrintablesImportModal({ isOpen, onClose, initialUrl }: PrintablesImportModalProps) {
  const queryClient = useQueryClient();
  const [url, setUrl] = useState('');
  const [preview, setPreview] = useState<PrintablesPreview | null>(null);
  const [selectedFileIds, setSelectedFileIds] = useState<string[]>([]);
  const [imageError, setImageError] = useState(false);
  const [step, setStep] = useState<'url' | 'confirm'>('url');
  const lastAutoPreviewUrl = useRef<string | null>(null);

  const previewMutation = useMutation({
    mutationFn: async (printablesUrl: string) => {
      const response = await apiClient.request<PrintablesPreview>({
        method: 'GET',
        url: `/3d-models/printables/preview`,
        params: { url: printablesUrl },
      });
      return response;
    },
    onSuccess: (data) => {
      setPreview(data);
      setImageError(false);
      setSelectedFileIds(data.files.length === 1 ? [data.files[0].id] : []);
      setStep('confirm');
    },
    onError: (err: Error) => {
      toast.error(`Preview failed: ${err.message}`);
    },
  });

  const importMutation = useMutation({
    mutationFn: async () => {
      if (!preview || selectedFileIds.length === 0) throw new Error('No files selected');
      const response = await apiClient.request<{ id: string }>({
        method: 'POST',
        url: `/3d-models/import/printables`,
        data: {
          url: preview.sourceUrl,
          fileIds: selectedFileIds,
        },
      });
      return response;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['file-browser'] });
      queryClient.invalidateQueries({ queryKey: ['3d-models'] });
      toast.success(`Imported ${selectedFileIds.length} file(s) from "${preview?.name}"`, {
        description: `by ${preview?.creator}`,
      });
      handleClose();
    },
    onError: (err: Error) => {
      toast.error(`Import failed: ${err.message}`);
    },
  });

  const handleClose = () => {
    setUrl('');
    setPreview(null);
    setSelectedFileIds([]);
    setImageError(false);
    setStep('url');
    lastAutoPreviewUrl.current = null;
    previewMutation.reset();
    importMutation.reset();
    onClose();
  };

  useEffect(() => {
    if (!isOpen) {
      return;
    }

    const normalizedUrl = initialUrl?.trim();
    if (!normalizedUrl || normalizedUrl === lastAutoPreviewUrl.current) {
      return;
    }

    lastAutoPreviewUrl.current = normalizedUrl;
    previewMutation.mutate(normalizedUrl);
  }, [initialUrl, isOpen, previewMutation]);

  const handlePreview = () => {
    if (!url.trim()) return;
    previewMutation.mutate(url.trim());
  };

  const handleBack = () => {
    setStep('url');
    setPreview(null);
    setSelectedFileIds([]);
    setImageError(false);
  };

  const handleImport = () => {
    importMutation.mutate();
  };

  const handleToggleFile = (fileId: string) => {
    setSelectedFileIds((current) =>
      current.includes(fileId)
        ? current.filter((id) => id !== fileId)
        : [...current, fileId],
    );
  };

  const handleToggleAllFiles = () => {
    if (!preview) return;

    setSelectedFileIds((current) =>
      current.length === preview.files.length
        ? []
        : preview.files.map((file) => file.id),
    );
  };

  const footer = step === 'url' ? (
    <div className="flex justify-end gap-2">
      <Button variant="secondary" onClick={handleClose}>Cancel</Button>
      <Button
        variant="primary"
        onClick={handlePreview}
        disabled={!url.trim() || previewMutation.isPending}
        loading={previewMutation.isPending}
      >
        Preview
      </Button>
    </div>
  ) : (
    <div className="flex justify-between gap-2">
      <Button variant="secondary" onClick={handleBack} disabled={importMutation.isPending}>
        Back
      </Button>
      <div className="flex gap-2">
        <Button variant="secondary" onClick={handleClose} disabled={importMutation.isPending}>
          Cancel
        </Button>
        <Button
          variant="primary"
          onClick={handleImport}
          disabled={selectedFileIds.length === 0 || importMutation.isPending}
          loading={importMutation.isPending}
        >
          Import
        </Button>
      </div>
    </div>
  );

  return (
    <Modal
      isOpen={isOpen}
      onClose={handleClose}
      title="Import from Printables"
      size="lg"
      footer={footer}
      isDisabled={importMutation.isPending}
    >
      {step === 'url' && (
        <div className="space-y-4">
          <p className="text-sm text-pf-text-secondary">
            Paste a Printables.com model URL to preview its files before importing.
          </p>
          <Input
            value={url}
            onChange={(e) => setUrl(e.target.value)}
            placeholder="https://www.printables.com/model/123456-model-name"
            onKeyDown={(e) => { if (e.key === 'Enter') handlePreview(); }}
            autoFocus
          />
          {previewMutation.isError && (
            <Alert variant="error">
              {previewMutation.error?.message || 'Failed to fetch preview'}
            </Alert>
          )}
        </div>
      )}

      {step === 'confirm' && preview && (
        <div className="space-y-4">
          {/* Model info header */}
          <div className="flex gap-4">
            {preview.thumbnailUrl && !imageError ? (
              <img
                src={preview.thumbnailUrl}
                alt={preview.name}
                className="w-24 h-24 rounded-lg object-cover shrink-0 bg-pf-bg-2"
                onError={() => setImageError(true)}
              />
            ) : (
              <div className="flex h-24 w-24 shrink-0 items-center justify-center rounded-lg border border-pf-border bg-pf-bg-2">
                <CubeIcon className="h-10 w-10 text-pf-text-tertiary opacity-50" ariaLabel={`${preview.name} preview unavailable`} />
              </div>
            )}
            <div className="min-w-0 space-y-1">
              <h3 className="text-base font-medium text-pf-text-primary truncate">
                {preview.name}
              </h3>
              <p className="text-sm text-pf-text-secondary">
                by {preview.creator}
              </p>
              {preview.license && (
                <p className="text-xs text-pf-text-tertiary">{preview.license}</p>
              )}
              <a
                href={preview.sourceUrl}
                target="_blank"
                rel="noopener noreferrer"
                className="inline-flex items-center gap-1 text-xs text-pf-accent hover:underline"
              >
                View on Printables
                <ExternalLinkIcon className="w-3 h-3" />
              </a>
            </div>
          </div>

          {/* File picker */}
          <div>
            <div className="mb-2 flex items-center justify-between gap-2">
              <p className="text-sm font-medium text-pf-text-primary">
                Select files to import:
              </p>
            </div>
            <div className="space-y-1 max-h-60 overflow-y-auto">
              {preview.files.length >= multiSelectThreshold && (
                <label className="flex items-center gap-3 rounded-lg border border-pf-border bg-pf-bg-1 p-2.5 transition-colors hover:bg-pf-bg-2">
                  <Checkbox
                    checked={selectedFileIds.length === preview.files.length}
                    indeterminate={selectedFileIds.length > 0 && selectedFileIds.length < preview.files.length}
                    onChange={handleToggleAllFiles}
                    disabled={importMutation.isPending}
                    aria-label="Select all files"
                  />
                  <span className="flex-1 text-sm font-medium text-pf-text-primary">
                    Select all files
                  </span>
                  <span className="text-xs text-pf-text-tertiary shrink-0">
                    {preview.files.length} total
                  </span>
                </label>
              )}
              {preview.files.map((file) => {
                const isSelected = selectedFileIds.includes(file.id);

                return (
                  <label
                    key={file.id}
                    className={`flex items-center gap-3 p-2.5 rounded-lg border cursor-pointer transition-colors ${
                      isSelected
                        ? 'border-pf-accent bg-pf-accent/5'
                        : 'border-pf-border hover:bg-pf-bg-2'
                    }`}
                  >
                    <Checkbox
                      checked={isSelected}
                      onChange={() => handleToggleFile(file.id)}
                      disabled={importMutation.isPending}
                      aria-label={`Select ${file.name}`}
                    />
                    <span className="flex-1 text-sm text-pf-text-primary truncate">
                      {file.name}
                    </span>
                    <span className="text-xs text-pf-text-tertiary shrink-0">
                      {formatFileSize(file.fileSize)}
                    </span>
                  </label>
                );
              })}
            </div>
          </div>

          {/* Import progress */}
          {importMutation.isPending && (
            <div className="flex items-center gap-2 text-sm text-pf-text-secondary">
              <Spinner size="sm" />
              <span>Downloading and importing file(s)…</span>
            </div>
          )}

          {importMutation.isError && (
            <Alert variant="error">
              {importMutation.error?.message || 'Import failed'}
            </Alert>
          )}
        </div>
      )}
    </Modal>
  );
}
