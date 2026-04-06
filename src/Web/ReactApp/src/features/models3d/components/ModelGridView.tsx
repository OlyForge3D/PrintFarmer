import React, { useState } from 'react';
import { useNavigate } from 'react-router';
import { toast } from 'sonner';
import { CubeIcon, EyeIcon, TagIcon, FileIcon, LayersTripleOutlineIcon, DeleteIcon, DownloadIcon } from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui';
import { ContextMenu } from '@/common/components/ContextMenu';
import { ConfirmationModal } from '@/common/components/modals/ConfirmationModal';
import { useContextMenu } from '@/common/hooks/useContextMenu';
import { apiClient } from '@/services/api';
import type { Model } from '@/types/models';
import type { ModelGridViewProps } from '@/types/components';

export const ModelGridView: React.FC<ModelGridViewProps> = ({
  models,
  isLoading,
  onViewerModel,
  onTagModel,
  formatFileSize
}) => {
  const navigate = useNavigate();
  const { position: contextMenuPosition, handleContextMenu, closeMenu, isOpen: isContextMenuOpen } = useContextMenu();
  const [selectedModelForDelete, setSelectedModelForDelete] = useState<Model | null>(null);
  const [selectedModelId, setSelectedModelId] = useState<string | null>(null);

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
    <>
      <div className="grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-4 xl:grid-cols-5 gap-3 overflow-y-auto">
        {models.map((model: Model) => {
          const isSelected = selectedModelId === model.id;
          
          const handleRightClick = (e: React.MouseEvent) => {
            setSelectedModelId(model.id);
            handleContextMenu(e);
          };

          return (
            <div
              key={model.id}
              className={`bg-pf-bg-1 rounded-lg border border-pf-border overflow-hidden hover:bg-pf-bg-secondary hover:shadow-lg transition-colors flex flex-col group ${
                isSelected ? 'ring-2 ring-pf-accent' : ''
              }`}
              onContextMenu={handleRightClick}
            >
              {/* Model Preview */}
              <div className="aspect-square bg-pf-bg-2 relative flex items-center justify-center min-h-32 overflow-hidden">
                {model.thumbnailPath ? (
                  <img
                    src={model.thumbnailPath}
                    alt={model.fileName}
                    className="w-full h-full object-contain group-hover:scale-105 transition-transform"
                  />
                ) : (
                  <CubeIcon className="w-12 h-12 text-pf-text-tertiary opacity-30" />
                )}
              </div>

              {/* Model Info */}
              <div className="p-2.5 flex-1 flex flex-col">
                <h3 className="font-semibold text-pf-text-primary line-clamp-2 mb-1.5 text-sm">{model.name}</h3>

                {/* Tags */}
                {model.tags && model.tags.length > 0 && (
                  <div className="flex flex-wrap gap-0.5 mb-2">
                    {model.tags.slice(0, 1).map(tag => (
                      <span
                        key={tag.id}
                        className="inline-block px-1.5 py-0.5 text-xs rounded-sm text-white"
                        style={{ backgroundColor: tag.color || 'var(--pf-accent)' }}
                      >
                        {tag.name}
                      </span>
                    ))}
                    {model.tags.length > 1 && (
                      <span className="inline-block px-1.5 py-0.5 text-xs rounded-sm bg-pf-bg-2 text-pf-text-secondary">
                        +{model.tags.length - 1}
                      </span>
                    )}
                  </div>
                )}

                {/* Metadata */}
                <div className="text-xs text-pf-text-secondary space-y-0.5 mb-2 flex-1">
                  {model.fileType && <div className="flex justify-between gap-1"><span>Type:</span> <span className="font-medium text-right">{model.fileType.toUpperCase()}</span></div>}
                  {typeof model.fileSize === 'number' && <div className="flex justify-between gap-1"><span>Size:</span> <span className="font-medium text-right">{formatFileSize(model.fileSize)}</span></div>}
                </div>

                {/* Actions */}
                <div className="flex gap-2">
                  <Button
                    onMouseEnter={() => {
                      // Preload hint for 3D viewer
                    }}
                    onClick={() => onViewerModel(model)}
                    variant="secondary"
                    size="sm"
                    className="flex-1"
                    title="View 3D Model"
                  >
                    <EyeIcon className="w-4 h-4" />
                  </Button>
                  <Button
                    onClick={() => navigate(`/models/${model.id}`)}
                    variant="secondary"
                    size="sm"
                    className="flex-1"
                    title="View Details"
                  >
                    <FileIcon className="w-4 h-4" />
                  </Button>
                  <Button
                    onClick={() => onTagModel(model)}
                    variant="secondary"
                    size="sm"
                    className="px-2"
                    title="Tag this model"
                  >
                    <TagIcon className="w-4 h-4" />
                  </Button>
                  <Button
                    onClick={() => navigate(`/slicer?modelId=${model.id}`)}
                    variant="primary"
                    size="sm"
                    className="flex-1"
                    title="Slice Model"
                  >
                    <LayersTripleOutlineIcon className="w-4 h-4" />
                  </Button>
                </div>
              </div>
            </div>
          );
        })}
      </div>

      {/* Context Menu */}
      {isContextMenuOpen && contextMenuPosition && (
        <ContextMenu
          x={contextMenuPosition.x}
          y={contextMenuPosition.y}
          items={selectedModelId ? 
          (models.find(m => m.id === selectedModelId))
            ? [
                {
                  label: 'Tag',
                  icon: TagIcon,
                  onClick: () => onTagModel(models.find(m => m.id === selectedModelId)!),
                },
                {
                  label: 'Download',
                  icon: DownloadIcon,
                  onClick: () => {
                    const model = models.find(m => m.id === selectedModelId);
                    if (model) {
                      const a = document.createElement('a');
                      a.href = `/api/3d-models/file/${model.id}`;
                      a.download = model.fileName;
                      a.click();
                    }
                  },
                },
                { divider: true },
                {
                  label: 'Delete',
                  icon: DeleteIcon,
                  variant: 'danger',
                  onClick: () => {
                    const model = models.find(m => m.id === selectedModelId);
                    if (model) setSelectedModelForDelete(model);
                  },
                },
              ]
            : []
          : []
        }
        onClose={closeMenu}
      />
      )}

      {/* Delete Confirmation Dialog */}
      <ConfirmationModal
        isOpen={!!selectedModelForDelete}
        title="Delete Model?"
        message={`Are you sure you want to delete "${selectedModelForDelete?.name}"? This action cannot be undone.`}
        confirmButtonText="Delete"
        cancelButtonText="Cancel"
        isDangerous={true}
        onConfirm={async () => {
          try {
            if (selectedModelForDelete?.id) {
              await apiClient.deleteModel3dFile(selectedModelForDelete.id);
              toast.success(`Deleted "${selectedModelForDelete.name}"`);
            }
            setSelectedModelForDelete(null);
          } catch (error) {
            const msg = error instanceof Error ? error.message : 'Failed to delete model';
            toast.error(msg);
          }
        }}
        onCancel={() => setSelectedModelForDelete(null)}
      />
    </>
  );
};
