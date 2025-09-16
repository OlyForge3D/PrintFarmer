import React, { useState, useCallback, Suspense } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { Upload, Box, Trash2, Eye, Settings } from 'lucide-react';
// Lazy load heavy three.js based viewers with manual preload support
import { lazyWithPreload } from '@/utils/lazyWithPreload';
import type { ModelViewerProps } from '@/components/3d/ModelViewer';
import type { GCodeViewerProps } from '@/components/3d/GCodeViewer';
const ModelViewer = lazyWithPreload<ModelViewerProps, React.FC<ModelViewerProps>>(
  () => import('@/components/3d/ModelViewer').then(m => ({ default: m.ModelViewer }))
);
const GCodeViewer = lazyWithPreload<GCodeViewerProps, React.FC<GCodeViewerProps>>(
  () => import('@/components/3d/GCodeViewer').then(m => ({ default: m.GCodeViewer }))
);
const SlicerConfigModal = lazyWithPreload<{
  isOpen: boolean;
  onClose: () => void;
  modelFile?: File;
  modelId?: string;
  modelName?: string;
  availablePrinters: { id: string; name: string; backend: string; isReachable: boolean }[];
  onSliceComplete?: (result: { jobId: string; gcodeUrl: string; printTime: number; filamentUsed: number }) => void;
}, React.FC<{
  isOpen: boolean;
  onClose: () => void;
  modelFile?: File;
  modelId?: string;
  modelName?: string;
  availablePrinters: { id: string; name: string; backend: string; isReachable: boolean }[];
  onSliceComplete?: (result: { jobId: string; gcodeUrl: string; printTime: number; filamentUsed: number }) => void;
}>>(
  () => import('@/components/slicer/SlicerConfigModal').then(m => ({ default: m.SlicerConfigModal }))
);
import { slicerService } from '@/services/slicerService';
import type { SlicedModelSummary } from '@/services/slicerService';
import { ViewerSkeleton } from '@/components/3d/ViewerSkeleton';

// Backend currently returns a SlicedModelSummary; we extend with optional UI enrichment fields.
type Model = SlicedModelSummary & {
  fileName?: string;
  fileSize?: number;
  fileType?: 'stl' | '3mf' | 'obj' | 'ply';
  uploadedAt?: string; // alias of createdAt
  url?: string;
  thumbnailUrl?: string;
};

interface GCodeFile {
  id: string;
  name: string;
  url: string;
  printTime?: number;
  filamentUsed?: number;
  layerCount?: number;
}

export const ModelsPage: React.FC = () => {
  const [dragOver, setDragOver] = useState(false);
  const [selectedFiles, setSelectedFiles] = useState<File[]>([]);
  const [viewerModel, setViewerModel] = useState<Model | null>(null);
  const [gcodeViewer, setGcodeViewer] = useState<GCodeFile | null>(null);
  const [slicerModal, setSlicerModal] = useState<{ 
    isOpen: boolean; 
    modelFile?: File; 
    modelId?: string; 
    modelName?: string; 
  }>({
    isOpen: false
  });
  const [uploadProgress, setUploadProgress] = useState<Record<string, number>>({});
  
  const queryClient = useQueryClient();

  // Fetch models
  const { data: models = [], isLoading } = useQuery<Model[]>({
    queryKey: ['models'],
    queryFn: () => slicerService.listModels(),
    staleTime: 2 * 60 * 1000, // Cache for 2 minutes
    gcTime: 5 * 60 * 1000 // Keep in cache for 5 minutes
  });

  // Fetch available printers for slicing (using fast endpoint without status checks)
  const { data: availablePrinters = [] } = useQuery({
    queryKey: ['printers-fast'],
    queryFn: async () => {
      const response = await fetch('/api/printers/fast');
      return response.json();
    },
    staleTime: 5 * 60 * 1000, // Cache for 5 minutes
    gcTime: 10 * 60 * 1000 // Keep in cache for 10 minutes
  });

  // Upload mutation
  const uploadMutation = useMutation({
    mutationFn: (file: File) => slicerService.uploadModel(file),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['models'] });
      setSelectedFiles([]);
      setUploadProgress({});
    }
  });

  // Delete mutation
  const deleteMutation = useMutation({
    mutationFn: (modelId: string) => slicerService.deleteModel(modelId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['models'] });
    }
  });

  const handleDrop = useCallback((e: React.DragEvent) => {
    e.preventDefault();
    setDragOver(false);
    
    const files = Array.from(e.dataTransfer.files).filter(file => 
      ['stl', '3mf', 'obj', 'ply'].includes(file.name.split('.').pop()?.toLowerCase() || '')
    );
    setSelectedFiles(prev => [...prev, ...files]);
  }, []);

  const handleFileSelect = (e: React.ChangeEvent<HTMLInputElement>) => {
    if (e.target.files) {
      const files = Array.from(e.target.files).filter(file => 
        ['stl', '3mf', 'obj', 'ply'].includes(file.name.split('.').pop()?.toLowerCase() || '')
      );
      setSelectedFiles(prev => [...prev, ...files]);
    }
  };

  const uploadFiles = async () => {
    for (const file of selectedFiles) {
      try {
        setUploadProgress(prev => ({ ...prev, [file.name]: 0 }));
        
        // Simulate progress for now (in real implementation, use XMLHttpRequest for progress)
        const progressInterval = setInterval(() => {
          setUploadProgress(prev => {
            const current = prev[file.name] || 0;
            if (current < 90) {
              return { ...prev, [file.name]: current + 10 };
            }
            return prev;
          });
        }, 200);

        await uploadMutation.mutateAsync(file);
        
        clearInterval(progressInterval);
        setUploadProgress(prev => ({ ...prev, [file.name]: 100 }));
      } catch (error) {
        console.error('Upload failed:', error);
        setUploadProgress(prev => {
          // eslint-disable-next-line @typescript-eslint/no-unused-vars
          const { [file.name]: _omit, ...rest } = prev; // remove failed file key
          return rest;
        });
      }
    }
  };

  const removeFile = (index: number) => {
    setSelectedFiles(prev => prev.filter((_, i) => i !== index));
  };

  const formatFileSize = (bytes: number) => {
    if (bytes === 0) return '0 Bytes';
    const k = 1024;
    const sizes = ['Bytes', 'KB', 'MB', 'GB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
  };

  // Removed unused getFileType helper

  if (isLoading) {
    return (
      <div className="flex items-center justify-center h-64">
        <div className="pf-animate-spin rounded-full h-12 w-12 border-b-2 border-pf-accent"></div>
      </div>
    );
  }

  return (
    <div className="space-y-6">
      {/* Header */}
      <div>
        <h1 className="text-2xl font-bold text-pf-text-primary">3D Models</h1>
        <p className="mt-1 text-pf-text-secondary">
          Upload and manage your 3D models for slicing and printing
        </p>
      </div>

      {/* Upload Area */}
      <div className="bg-pf-bg-1 rounded-lg shadow-lg border border-pf-border">
        <div
          className={`border-2 border-dashed rounded-lg p-8 text-center transition-colors ${
            dragOver ? 'border-pf-accent bg-pf-accent-bg bg-opacity-20' : 'border-pf-border'
          }`}
          onDragOver={(e) => { e.preventDefault(); setDragOver(true); }}
          onDragLeave={() => setDragOver(false)}
          onDrop={handleDrop}
        >
          <div className="space-y-4">
            <div className="mx-auto w-16 h-16 bg-pf-bg-2 rounded-full flex items-center justify-center">
              <Box className="w-8 h-8 text-pf-text-tertiary" />
            </div>
            
            <div>
              <label htmlFor="file-upload" className="cursor-pointer">
                <span className="text-lg font-medium text-pf-text-primary">
                  Drop 3D models here or click to select
                </span>
              </label>
              <p className="text-pf-text-secondary mt-1">
                Supports STL, 3MF, OBJ, and PLY files
              </p>
              <input
                id="file-upload"
                type="file"
                multiple
                accept=".stl,.3mf,.obj,.ply"
                onChange={handleFileSelect}
                className="hidden"
              />
            </div>
          </div>
        </div>

        {/* Selected files */}
        {selectedFiles.length > 0 && (
          <div className="border-t border-pf-border p-4">
            <h4 className="font-medium mb-3 text-pf-text-primary">Selected Files</h4>
            <div className="space-y-2">
              {selectedFiles.map((file, index) => (
                <div key={index} className="flex items-center justify-between bg-pf-bg-2 p-3 rounded">
                  <div className="flex items-center space-x-3">
                    <Box className="w-5 h-5 text-pf-text-tertiary" />
                    <div>
                      <div className="font-medium text-sm text-pf-text-primary">{file.name}</div>
                      <div className="text-xs text-pf-text-secondary">{formatFileSize(file.size)}</div>
                    </div>
                  </div>
                  <div className="flex items-center space-x-2">
                    {uploadProgress[file.name] !== undefined && (
                      <div className="w-24">
                        <div className="text-xs text-pf-text-secondary mb-1">
                          {uploadProgress[file.name]}%
                        </div>
                        <div className="w-full bg-pf-bg-0 rounded-full h-1 border border-pf-border">
                          {(() => {
                            const pct = uploadProgress[file.name] ?? 0;
                            const bucket = Math.min(100, Math.max(0, Math.round(pct / 5) * 5));
                            const widthClass = `w-[${bucket}%]` as const; // Tailwind arbitrary width
                            return (
                              <div
                                className={`bg-pf-accent h-1 rounded-full transition-all duration-300 ${widthClass}`}
                                aria-label={`Upload progress ${pct} percent`}
                              />
                            );
                          })()}
                        </div>
                      </div>
                    )}
                    <button
                      onClick={() => removeFile(index)}
                      className="p-1 hover:bg-pf-bg-1 rounded"
                      aria-label="Remove file"
                      title="Remove file"
                    >
                      <Trash2 className="w-4 h-4 text-pf-text-tertiary" />
                    </button>
                  </div>
                </div>
              ))}
            </div>
            <div className="mt-4 flex justify-end">
              <button
                onClick={uploadFiles}
                disabled={uploadMutation.isPending}
                className="px-4 py-2 bg-pf-accent text-white rounded hover:bg-pf-success-hover disabled:opacity-50"
              >
                <Upload className="w-4 h-4 inline mr-2" />
                {uploadMutation.isPending ? 'Uploading...' : 'Upload Files'}
              </button>
            </div>
          </div>
        )}
      </div>

      {/* Models Grid */}
      <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-6">
        {models.map((model: Model) => (
          <div key={model.id} className="bg-pf-bg-1 rounded-lg shadow-lg border border-pf-border overflow-hidden">
            {/* Model Preview */}
            <div className="h-48 bg-pf-bg-2 relative">
              {model.thumbnailUrl ? (
                <img 
                  src={model.thumbnailUrl} 
                  alt={model.name}
                  className="w-full h-full object-cover"
                />
              ) : (
                <div className="w-full h-full flex items-center justify-center">
                  <Box className="w-12 h-12 text-pf-text-tertiary" />
                </div>
              )}
              
              {/* Quick actions overlay */}
              <div className="absolute top-2 right-2 flex space-x-1">
                <button
                  onMouseEnter={() => (ModelViewer as typeof ModelViewer).preload?.()}
                  onFocus={() => (ModelViewer as typeof ModelViewer).preload?.()}
                  onClick={() => setViewerModel(model)}
                  className="p-2 bg-pf-bg-1 bg-opacity-80 hover:bg-pf-bg-1 rounded shadow border border-pf-border"
                  title="View 3D Model"
                >
                  <Eye className="w-4 h-4 text-pf-text-primary" />
                </button>
              </div>
            </div>

            {/* Model Info */}
            <div className="p-4">
              <h3 className="font-medium text-lg mb-1 text-pf-text-primary">{model.name}</h3>
              <div className="text-sm text-pf-text-secondary space-y-1">
                {model.fileType && <div>Type: {model.fileType.toUpperCase()}</div>}
                {typeof model.fileSize === 'number' && <div>Size: {formatFileSize(model.fileSize)}</div>}
                <div>Uploaded: {new Date(model.uploadedAt || (model as { createdAt?: string; updatedAt?: string }).createdAt || (model as { updatedAt?: string }).updatedAt || Date.now()).toLocaleDateString()}</div>
              </div>

              {/* Actions */}
              <div className="mt-4 flex space-x-2">
                <button
                  onMouseEnter={() => (SlicerConfigModal as typeof SlicerConfigModal).preload?.()}
                  onFocus={() => (SlicerConfigModal as typeof SlicerConfigModal).preload?.()}
                  onClick={() => setSlicerModal({ 
                    isOpen: true, 
                    modelId: model.id,
                    modelName: model.name
                  })}
                  className="flex-1 px-3 py-2 bg-pf-accent-bg bg-opacity-20 text-pf-accent rounded hover:bg-pf-accent-bg hover:bg-opacity-30 text-sm font-medium border border-pf-accent"
                >
                  <Settings className="w-4 h-4 inline mr-1" />
                  Slice
                </button>
                <button
                  onClick={() => deleteMutation.mutate(model.id)}
                  disabled={deleteMutation.isPending}
                  className="px-3 py-2 bg-pf-error-bg text-pf-error-text rounded hover:bg-pf-error border border-pf-error-border"
                  title="Delete Model"
                >
                  <Trash2 className="w-4 h-4" />
                </button>
              </div>
            </div>
          </div>
        ))}
      </div>

      {/* Model Viewer Modal */}
      {viewerModel && (
        <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50 p-4">
          <div className="bg-pf-bg-1 rounded-lg shadow-xl max-w-4xl w-full max-h-[90vh] overflow-y-auto border border-pf-border">
            <div className="flex items-center justify-between p-4 border-b border-pf-border">
              <h3 className="font-medium text-lg text-pf-text-primary">{viewerModel.name}</h3>
              <button
                onClick={() => setViewerModel(null)}
                className="p-1 hover:bg-pf-bg-2 rounded text-pf-text-primary"
              >
                ×
              </button>
            </div>
            <div className="p-4">
              <Suspense fallback={<ViewerSkeleton variant="model" />}> 
                {viewerModel.url && viewerModel.fileType && (
                  <ModelViewer
                    modelUrl={viewerModel.url}
                    fileType={viewerModel.fileType}
                    className="h-96 w-full"
                  />
                )}
              </Suspense>
            </div>
          </div>
        </div>
      )}

      {/* G-code Viewer Modal */}
      {gcodeViewer && (
        <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50 p-4">
          <div className="bg-pf-bg-1 rounded-lg shadow-xl max-w-6xl w-full max-h-[90vh] overflow-y-auto border border-pf-border">
            <div className="flex items-center justify-between p-4 border-b border-pf-border">
              <h3 className="font-medium text-lg text-pf-text-primary">{gcodeViewer.name}</h3>
              <button
                onClick={() => setGcodeViewer(null)}
                className="p-1 hover:bg-pf-bg-2 rounded text-pf-text-primary"
              >
                ×
              </button>
            </div>
            <div className="p-4">
              <Suspense fallback={<ViewerSkeleton variant="gcode" />}> 
                <GCodeViewer gcodeUrl={gcodeViewer.url} />
              </Suspense>
            </div>
          </div>
        </div>
      )}

      {/* Slicer Configuration Modal */}
      {slicerModal.isOpen && (
        <Suspense fallback={<div>Loading slicer...</div>}>
          <SlicerConfigModal
            isOpen={slicerModal.isOpen}
            onClose={() => setSlicerModal({ isOpen: false })}
            modelFile={slicerModal.modelFile}
            modelId={slicerModal.modelId}
            modelName={slicerModal.modelName}
            availablePrinters={availablePrinters}
            onSliceComplete={(result: { jobId: string; gcodeUrl: string; printTime: number; filamentUsed: number }) => {
              console.log('Slicing completed:', result);
              // Could navigate to G-code viewer or print queue
            }}
          />
        </Suspense>
      )}
    </div>
  );
};