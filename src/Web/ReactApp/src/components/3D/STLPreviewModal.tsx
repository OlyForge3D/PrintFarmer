import React, { useState, useEffect } from 'react';
import { STLViewer } from './STLViewer';
import * as THREE from 'three';

interface STLPreviewModalProps {
  isOpen: boolean;
  file?: File | null;
  fileUrl?: string;
  fileName?: string;
  onClose: () => void;
  onUseModel?: () => void;
}

/**
 * STL Preview Modal Component
 * Displays an STL file (File or URL) in a modal with controls and file information
 */
export const STLPreviewModal: React.FC<STLPreviewModalProps> = ({
  isOpen,
  file,
  fileUrl,
  fileName,
  onClose,
  onUseModel,
}) => {
  const [modelInfo, setModelInfo] = useState<{
    vertices: number;
    triangles: number;
    fileSize: string;
    format: string;
  } | null>(null);
  const [isLoading, setIsLoading] = useState(false);

  useEffect(() => {
    if (file) {
      const getFileInfo = async () => {
        try {
          const arrayBuffer = await file.arrayBuffer();
          const view = new DataView(arrayBuffer);
          const triangles = view.getUint32(80, true);
          const vertices = triangles * 3;
          const fileSize = (file.size / 1024 / 1024).toFixed(2);

          setModelInfo({
            vertices,
            triangles,
            fileSize: `${fileSize} MB`,
            format: file.name.endsWith('.stl') ? 'STL' : 'Unknown',
          });
        } catch (error) {
          console.error('Error reading file info:', error);
        }
      };

      getFileInfo();
    } else if (fileUrl && fileName) {
      // For URL-based files, estimate info from file name
      setIsLoading(true);
      fetch(fileUrl)
        .then(res => res.blob())
        .then(blob => {
          const fileSize = (blob.size / 1024 / 1024).toFixed(2);
          setModelInfo({
            vertices: 0,
            triangles: 0,
            fileSize: `${fileSize} MB`,
            format: fileName.endsWith('.stl') ? 'STL' : fileName.split('.').pop()?.toUpperCase() || 'Unknown',
          });
          setIsLoading(false);
        })
        .catch(error => {
          console.error('Error fetching file info:', error);
          setIsLoading(false);
        });
    }
  }, [file, fileUrl, fileName]);

  if (!isOpen || (!file && !fileUrl)) {
    return null;
  }

  const displayFileName = fileName || file?.name || 'Unnamed Model';
  const shouldShowViewer = file || fileUrl;

  return (
    <div className="fixed inset-0 bg-black bg-opacity-50 z-50 flex items-center justify-center p-4">
      <div className="bg-gray-900 rounded-lg shadow-2xl w-full max-w-4xl max-h-[90vh] overflow-hidden flex flex-col border border-gray-700">
        {/* Header */}
        <div className="bg-gradient-to-r from-gray-800 to-gray-900 px-6 py-4 border-b border-gray-700 flex items-center justify-between">
          <div>
            <h2 className="text-xl font-bold text-white">STL Model Preview</h2>
            <p className="text-sm text-gray-400 mt-1">{displayFileName}</p>
          </div>
          <button
            onClick={onClose}
            className="text-gray-400 hover:text-white transition-colors p-2 hover:bg-gray-700 rounded"
            aria-label="Close"
          >
            <svg className="w-6 h-6" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
            </svg>
          </button>
        </div>

        {/* Content */}
        <div className="flex-1 flex gap-4 p-4 min-h-0">
          {/* 3D Viewer */}
          <div className="flex-1 bg-gradient-to-b from-gray-800 to-gray-900 rounded-lg overflow-hidden border border-gray-700">
            {shouldShowViewer && (
              file ? (
                <STLViewer file={file} autoRotate={false} cameraPosition={[0, 0, 150]} />
              ) : fileUrl ? (
                <STLViewer file={fileUrl} autoRotate={false} cameraPosition={[0, 0, 150]} />
              ) : null
            )}
          </div>

          {/* Info Panel */}
          <div className="w-64 bg-gray-800 rounded-lg p-4 border border-gray-700 overflow-y-auto">
            <h3 className="text-lg font-semibold text-white mb-4">Model Information</h3>

            {modelInfo ? (
              <div className="space-y-4">
                {/* File Size */}
                <div>
                  <label className="block text-sm text-gray-400 mb-1">File Size</label>
                  <p className="text-lg text-white font-mono">{modelInfo.fileSize}</p>
                </div>

                {/* Triangle Count */}
                {modelInfo.triangles > 0 && (
                  <div>
                    <label className="block text-sm text-gray-400 mb-1">Triangles</label>
                    <p className="text-lg text-white font-mono">{modelInfo.triangles.toLocaleString()}</p>
                  </div>
                )}

                {/* Vertex Count */}
                {modelInfo.vertices > 0 && (
                  <div>
                    <label className="block text-sm text-gray-400 mb-1">Vertices</label>
                    <p className="text-lg text-white font-mono">{modelInfo.vertices.toLocaleString()}</p>
                  </div>
                )}

                {/* Format */}
                <div>
                  <label className="block text-sm text-gray-400 mb-1">Format</label>
                  <p className="text-lg text-white font-mono">{modelInfo.format}</p>
                </div>

                {/* Separator */}
                <div className="border-t border-gray-700 my-2"></div>

                {/* Controls Info */}
                <div className="bg-gray-900 rounded p-3">
                  <h4 className="text-sm font-semibold text-white mb-2">Controls</h4>
                  <div className="text-xs text-gray-300 space-y-1">
                    <p><span className="text-blue-400">Left Click + Drag</span> - Rotate</p>
                    <p><span className="text-blue-400">Right Click + Drag</span> - Pan</p>
                    <p><span className="text-blue-400">Scroll</span> - Zoom</p>
                    <p><span className="text-blue-400">Double Click</span> - Reset View</p>
                  </div>
                </div>
              </div>
            ) : isLoading ? (
              <div className="flex items-center justify-center h-full">
                <div className="text-center">
                  <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-blue-500 mx-auto"></div>
                  <p className="text-sm text-gray-400 mt-2">Loading info...</p>
                </div>
              </div>
            ) : (
              <p className="text-sm text-gray-400">Unable to load model information</p>
            )}
          </div>
        </div>

        {/* Footer */}
        <div className="bg-gray-800 px-6 py-3 border-t border-gray-700 flex justify-end gap-3">
          <button
            onClick={onClose}
            className="px-4 py-2 bg-gray-700 hover:bg-gray-600 text-white rounded-lg transition-colors font-medium"
          >
            Close
          </button>
          {onUseModel && (
            <button
              onClick={onUseModel}
              className="px-4 py-2 bg-blue-600 hover:bg-blue-700 text-white rounded-lg transition-colors font-medium"
            >
              Use This Model
            </button>
          )}
        </div>
      </div>
    </div>
  );
};

export default STLPreviewModal;
