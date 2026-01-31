import React from 'react';
import { useNavigate, Link } from 'react-router';
import { CubeIcon, EyeIcon, TagIcon, FileIcon, LayersTripleOutlineIcon } from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui';
import { SelectableRow } from '@/common/components/Table/SelectableRow';
import type { Model } from '@/types/models';
import type { ModelListViewProps } from '@/types/components';

export const ModelListView: React.FC<ModelListViewProps> = ({
  models,
  isLoading,
  onViewerModel,
  onTagModel,
  formatFileSize
}) => {
  const navigate = useNavigate();

  if (isLoading && models.length === 0) {
    return (
      <div className="flex items-center justify-center h-full">
        <div className="text-center">
          <p className="text-pf-text-secondary">Loading models...</p>
        </div>
      </div>
    );
  }

  if (models.length === 0) {
    return (
      <div className="flex items-center justify-center h-full">
        <div className="text-center">
          <CubeIcon className="w-12 h-12 text-pf-text-tertiary opacity-30 mx-auto mb-3" />
          <p className="text-pf-text-secondary">No models found</p>
        </div>
      </div>
    );
  }

  return (
    <div className="bg-pf-bg-1 rounded-lg border border-pf-border overflow-x-auto flex-1">
      <table className="w-full text-sm">
        <thead>
          <tr className="border-b border-pf-border bg-pf-bg-2 sticky top-0">
            <th className="px-4 py-3 text-left font-semibold text-pf-text-primary">Name</th>
            <th className="px-4 py-3 text-left font-semibold text-pf-text-primary">Type</th>
            <th className="px-4 py-3 text-left font-semibold text-pf-text-primary">Size</th>
            <th className="px-4 py-3 text-left font-semibold text-pf-text-primary">Tags</th>
            <th className="px-4 py-3 text-left font-semibold text-pf-text-primary">Uploaded</th>
            <th className="px-4 py-3 text-right font-semibold text-pf-text-primary">Actions</th>
          </tr>
        </thead>
        <tbody className="divide-y divide-pf-border">
          {models.map((model: Model) => (
            <SelectableRow key={model.id} isSelected={false}>
              <td className="px-4 py-3">
                <div className="flex items-center gap-3 min-w-0">
                  {/* Thumbnail */}
                  <div className="w-12 h-12 shrink-0 rounded-sm bg-pf-bg-2 flex items-center justify-center border border-pf-border overflow-hidden">
                    {model.thumbnailPath ? (
                      <img
                        src={model.thumbnailPath}
                        alt={model.fileName}
                        className="w-full h-full object-contain"
                      />
                    ) : (
                      <CubeIcon className="w-6 h-6 text-pf-text-tertiary opacity-50" />
                    )}
                  </div>
                  {/* Filename as link, not button */}
                  <Link
                    to={`/models/${model.id}`}
                    className="font-medium text-pf-accent hover:underline text-left min-w-0 truncate"
                  >
                    {model.name}
                  </Link>
                </div>
              </td>
              <td className="px-4 py-3 text-pf-text-secondary text-xs font-medium">
                {model.fileType?.toUpperCase() || '—'}
              </td>
              <td className="px-4 py-3 text-pf-text-secondary text-xs">
                {typeof model.fileSize === 'number' ? formatFileSize(model.fileSize) : '—'}
              </td>
              <td className="px-4 py-3">
                {model.tags && model.tags.length > 0 ? (
                  <div className="flex flex-wrap gap-1">
                    {model.tags.slice(0, 2).map(tag => (
                      <span
                        key={tag.id}
                        className="inline-block px-2 py-0.5 text-xs rounded-sm text-white"
                        style={{ backgroundColor: tag.color || 'var(--pf-accent)' }}
                      >
                        {tag.name}
                      </span>
                    ))}
                    {model.tags.length > 2 && (
                      <span className="text-xs text-pf-text-secondary">+{model.tags.length - 2}</span>
                    )}
                  </div>
                ) : (
                  <span className="text-pf-text-tertiary">—</span>
                )}
              </td>
              <td className="px-4 py-3 text-pf-text-secondary text-xs">
                {new Date(model.uploadedAt).toLocaleDateString()}
              </td>
              <td className="px-4 py-3 text-right">
                <div className="flex justify-end gap-2">
                  {model.fileType !== '3mf' && (
                    <Button
                      onMouseEnter={() => {
                        // Preload hint for 3D viewer
                      }}
                      onClick={() => onViewerModel(model)}
                      variant="subtle"
                      size="sm"
                      title="View 3D Model"
                    >
                      <EyeIcon className="w-4 h-4" />
                    </Button>
                  )}
                  <Button
                    onClick={() => navigate(`/models/${model.id}`)}
                    variant="subtle"
                    size="sm"
                    title="View Details"
                  >
                    <FileIcon className="w-4 h-4" />
                  </Button>
                  <Button
                    onClick={() => onTagModel(model)}
                    variant="subtle"
                    size="sm"
                    title="Tag this model"
                  >
                    <TagIcon className="w-4 h-4" />
                  </Button>
                  <Button
                    onClick={() => navigate(`/jobs/new?modelId=${model.id}`)}
                    variant="subtle"
                    size="sm"
                    title="Slice Model"
                  >
                    <LayersTripleOutlineIcon className="w-4 h-4" />
                  </Button>
                </div>
              </td>
            </SelectableRow>
          ))}
        </tbody>
      </table>
    </div>
  );
};
