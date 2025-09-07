import React, { useState, useCallback, Suspense } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { Upload, Box, Trash2, Eye, Settings } from 'lucide-react';
// Lazy load heavy three.js based viewers; map named export to default expected by React.lazy
const ModelViewer = React.lazy(() => import('@/components/3d/ModelViewer').then(m => ({ default: m.ModelViewer })));
const GCodeViewer = React.lazy(() => import('@/components/3d/GCodeViewer').then(m => ({ default: m.GCodeViewer })));
import { SlicerConfigModal } from '@/components/slicer/SlicerConfigModal';
import { slicerService } from '@/services/slicerService';

interface Model {
  id: string;
  name: string;
  fileName: string;
  fileSize: number;
  fileType: 'stl' | '3mf' | 'obj' | 'ply';
  uploadedAt: string;
  url: string;
  thumbnailUrl?: string;
}

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
  const [slicerModal, setSlicerModal] = useState<{ isOpen: boolean; model: File | null }>({
    isOpen: false,
    model: null
  });
  const [uploadProgress, setUploadProgress] = useState<Record<string, number>>({});
  
  const queryClient = useQueryClient();

  // Fetch models
  const { data: models = [], isLoading } = useQuery({
    queryKey: ['models'],
    queryFn: slicerService.listModels
  });

  // Fetch available printers for slicing
  const { data: availablePrinters = [] } = useQuery({
    queryKey: ['printers'],
    queryFn: async () => {
      const response = await fetch('/api/printers');
      return response.json();
    }
  });

  // Upload mutation
  const uploadMutation = useMutation({
    mutationFn: slicerService.uploadModel,
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['models'] });
      setSelectedFiles([]);
      setUploadProgress({});
    }
  });

  // Delete mutation
  const deleteMutation = useMutation({
    mutationFn: slicerService.deleteModel,
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
        <div className="animate-spin rounded-full h-12 w-12 border-b-2 border-blue-600"></div>
      </div>
    );
  }

  return (
    <div className="space-y-6">
      {/* Header */}
      <div>
        <h1 className="text-2xl font-bold text-gray-900">3D Models</h1>
        <p className="mt-1 text-gray-500">
          Upload and manage your 3D models for slicing and printing
        </p>
      </div>

      {/* Upload Area */}
      <div className="bg-white rounded-lg shadow">
        <div
          className={`border-2 border-dashed rounded-lg p-8 text-center transition-colors ${
            dragOver ? 'border-blue-500 bg-blue-50' : 'border-gray-300'
          }`}
          onDragOver={(e) => { e.preventDefault(); setDragOver(true); }}
          onDragLeave={() => setDragOver(false)}
          onDrop={handleDrop}
        >
          <div className="space-y-4">
            <div className="mx-auto w-16 h-16 bg-gray-100 rounded-full flex items-center justify-center">
              <Box className="w-8 h-8 text-gray-400" />
            </div>
            
            <div>
              <label htmlFor="file-upload" className="cursor-pointer">
                <span className="text-lg font-medium text-gray-900">
                  Drop 3D models here or click to select
                </span>
              </label>
              <p className="text-gray-500 mt-1">
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
          <div className="border-t p-4">
            <h4 className="font-medium mb-3">Selected Files</h4>
            <div className="space-y-2">
              {selectedFiles.map((file, index) => (
                <div key={index} className="flex items-center justify-between bg-gray-50 p-3 rounded">
                  <div className="flex items-center space-x-3">
                    <Box className="w-5 h-5 text-gray-400" />
                    <div>
                      <div className="font-medium text-sm">{file.name}</div>
                      <div className="text-xs text-gray-500">{formatFileSize(file.size)}</div>
                    </div>
                  </div>
                  <div className="flex items-center space-x-2">
                    {uploadProgress[file.name] !== undefined && (
                      <div className="w-24">
                        <div className="text-xs text-gray-600 mb-1">
                          {uploadProgress[file.name]}%
                        </div>
                        <div className="w-full bg-gray-200 rounded-full h-1">
                          {(() => {
                            const pct = uploadProgress[file.name] ?? 0;
                            const bucket = Math.min(100, Math.max(0, Math.round(pct / 5) * 5));
                            const widthClass = `w-[${bucket}%]` as const; // Tailwind arbitrary width
                            return (
                              <div
                                className={`bg-blue-600 h-1 rounded-full transition-all duration-300 ${widthClass}`}
                                aria-label={`Upload progress ${pct} percent`}
                              />
                            );
                          })()}
                        </div>
                      </div>
                    )}
                    <button
                      onClick={() => removeFile(index)}
                      className="p-1 hover:bg-gray-200 rounded"
                      aria-label="Remove file"
                      title="Remove file"
                    >
                      <Trash2 className="w-4 h-4 text-gray-500" />
                    </button>
                  </div>
                </div>
              ))}
            </div>
            <div className="mt-4 flex justify-end">
              <button
                onClick={uploadFiles}
                disabled={uploadMutation.isPending}
                className="px-4 py-2 bg-blue-600 text-white rounded hover:bg-blue-700 disabled:opacity-50"
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
          <div key={model.id} className="bg-white rounded-lg shadow-md overflow-hidden">
            {/* Model Preview */}
            <div className="h-48 bg-gray-100 relative">
              {model.thumbnailUrl ? (
                <img 
                  src={model.thumbnailUrl} 
                  alt={model.name}
                  className="w-full h-full object-cover"
                />
              ) : (
                <div className="w-full h-full flex items-center justify-center">
                  <Box className="w-12 h-12 text-gray-400" />
                </div>
              )}
              
              {/* Quick actions overlay */}
              <div className="absolute top-2 right-2 flex space-x-1">
                <button
                  onClick={() => setViewerModel(model)}
                  className="p-2 bg-white/80 hover:bg-white rounded shadow"
                  title="View 3D Model"
                >
                  <Eye className="w-4 h-4" />
                </button>
              </div>
            </div>

            {/* Model Info */}
            <div className="p-4">
              <h3 className="font-medium text-lg mb-1">{model.name}</h3>
              <div className="text-sm text-gray-500 space-y-1">
                <div>Type: {model.fileType.toUpperCase()}</div>
                <div>Size: {formatFileSize(model.fileSize)}</div>
                <div>Uploaded: {new Date(model.uploadedAt).toLocaleDateString()}</div>
              </div>

              {/* Actions */}
              <div className="mt-4 flex space-x-2">
                <button
                  onClick={() => setSlicerModal({ 
                    isOpen: true, 
                    model: new File([], model.fileName) // Simplified for demo
                  })}
                  className="flex-1 px-3 py-2 bg-blue-100 text-blue-700 rounded hover:bg-blue-200 text-sm font-medium"
                >
                  <Settings className="w-4 h-4 inline mr-1" />
                  Slice
                </button>
                <button
                  onClick={() => deleteMutation.mutate(model.id)}
                  disabled={deleteMutation.isPending}
                  className="px-3 py-2 bg-red-100 text-red-700 rounded hover:bg-red-200"
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
          <div className="bg-white rounded-lg shadow-xl max-w-4xl w-full max-h-[90vh] overflow-y-auto">
            <div className="flex items-center justify-between p-4 border-b">
              <h3 className="font-medium text-lg">{viewerModel.name}</h3>
              <button
                onClick={() => setViewerModel(null)}
                className="p-1 hover:bg-gray-100 rounded"
              >
                ×
              </button>
            </div>
            <div className="p-4">
              <Suspense fallback={<div className="h-96 w-full flex items-center justify-center">Loading 3D Viewer...</div>}>
                <ModelViewer
                  modelUrl={viewerModel.url}
                  fileType={viewerModel.fileType}
                  className="h-96 w-full"
                />
              </Suspense>
            </div>
          </div>
        </div>
      )}

      {/* G-code Viewer Modal */}
      {gcodeViewer && (
        <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50 p-4">
          <div className="bg-white rounded-lg shadow-xl max-w-6xl w-full max-h-[90vh] overflow-y-auto">
            <div className="flex items-center justify-between p-4 border-b">
              <h3 className="font-medium text-lg">{gcodeViewer.name}</h3>
              <button
                onClick={() => setGcodeViewer(null)}
                className="p-1 hover:bg-gray-100 rounded"
              >
                ×
              </button>
            </div>
            <div className="p-4">
              <Suspense fallback={<div className="h-96 w-full flex items-center justify-center">Loading G-code Viewer...</div>}>
                <GCodeViewer gcodeUrl={gcodeViewer.url} />
              </Suspense>
            </div>
          </div>
        </div>
      )}

      {/* Slicer Configuration Modal */}
      <SlicerConfigModal
        isOpen={slicerModal.isOpen}
        onClose={() => setSlicerModal({ isOpen: false, model: null })}
        modelFile={slicerModal.model!}
        availablePrinters={availablePrinters}
        onSliceComplete={(result) => {
          console.log('Slicing completed:', result);
          // Could navigate to G-code viewer or print queue
        }}
      />
    </div>
  );
};