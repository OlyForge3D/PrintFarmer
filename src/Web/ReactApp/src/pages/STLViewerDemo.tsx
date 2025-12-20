import React, { useState } from 'react';
import { STLViewer } from '../components/3D/STLViewer';
import { STLPreviewModal } from '../components/3D/STLPreviewModal';
import { useSTLFile } from '../hooks/useSTLFile';

/**
 * STL Viewer Demo Page
 * Demonstrates Phase 1 implementation of STL file preview functionality
 */
export const STLViewerDemo: React.FC = () => {
  const { file, fileInfo, errors, isLoading, selectFile, clearFile } = useSTLFile(50);
  const [showModal, setShowModal] = useState(false);
  const fileInputRef = React.useRef<HTMLInputElement>(null);

  const handleFileSelect = async (event: React.ChangeEvent<HTMLInputElement>) => {
    const selectedFile = event.target.files?.[0];
    if (selectedFile) {
      await selectFile(selectedFile);
      if (!errors.length) {
        setShowModal(true);
      }
    }
  };

  const handleDrop = async (event: React.DragEvent<HTMLDivElement>) => {
    event.preventDefault();
    const droppedFile = event.dataTransfer.files?.[0];
    if (droppedFile?.name.toLowerCase().endsWith('.stl')) {
      await selectFile(droppedFile);
      if (!errors.length) {
        setShowModal(true);
      }
    }
  };

  return (
    <div className="min-h-screen bg-gradient-to-br from-gray-900 via-gray-800 to-gray-900 p-6">
      <div className="max-w-6xl mx-auto">
        {/* Header */}
        <div className="mb-8">
          <h1 className="text-4xl font-bold text-white mb-2">STL File Viewer</h1>
          <p className="text-gray-400">PrintFarmer Phase 1: 3D Model Preview</p>
        </div>

        {/* Upload Area */}
        <div className="mb-8">
          <div
            onDrop={handleDrop}
            onDragOver={(e) => e.preventDefault()}
            onClick={() => fileInputRef.current?.click()}
            className="border-2 border-dashed border-gray-600 rounded-lg p-12 text-center cursor-pointer hover:border-blue-500 hover:bg-blue-500 hover:bg-opacity-5 transition-all"
          >
            <svg
              className="mx-auto h-12 w-12 text-gray-400 mb-4"
              stroke="currentColor"
              fill="none"
              viewBox="0 0 48 48"
            >
              <path
                d="M28 8H12a4 4 0 00-4 4v20a4 4 0 004 4h24a4 4 0 004-4V20m-6-10l-6-6m0 0l-6 6m6-6v18"
                strokeWidth={2}
                strokeLinecap="round"
                strokeLinejoin="round"
              />
            </svg>
            <h3 className="text-xl font-semibold text-white mb-2">Upload STL File</h3>
            <p className="text-gray-400 mb-2">Drag and drop your STL file here or click to browse</p>
            <p className="text-sm text-gray-500">Maximum file size: 50 MB</p>
          </div>
          <input
            ref={fileInputRef}
            type="file"
            accept=".stl"
            onChange={handleFileSelect}
            className="hidden"
          />
        </div>

        {/* Error Messages */}
        {errors.length > 0 && (
          <div className="bg-red-900 bg-opacity-30 border border-red-700 rounded-lg p-4 mb-6">
            <h3 className="text-red-300 font-semibold mb-2">Error</h3>
            <ul className="space-y-1">
              {errors.map((error, index) => (
                <li key={index} className="text-red-200 text-sm">
                  • {error}
                </li>
              ))}
            </ul>
          </div>
        )}

        {/* Loading State */}
        {isLoading && (
          <div className="bg-blue-900 bg-opacity-30 border border-blue-700 rounded-lg p-4 mb-6 flex items-center gap-3">
            <div className="animate-spin rounded-full h-5 w-5 border-b-2 border-blue-500"></div>
            <span className="text-blue-300">Processing file...</span>
          </div>
        )}

        {/* File Info and Viewer */}
        {file && !errors.length && (
          <div className="space-y-6">
            {/* File Information Card */}
            <div className="bg-gray-800 rounded-lg p-6 border border-gray-700">
              <h2 className="text-2xl font-bold text-white mb-4">File Information</h2>
              <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
                <div>
                  <label className="block text-sm text-gray-400 mb-1">File Name</label>
                  <p className="text-white font-mono text-sm break-all">{fileInfo?.name}</p>
                </div>
                <div>
                  <label className="block text-sm text-gray-400 mb-1">File Size</label>
                  <p className="text-white font-mono">{fileInfo?.sizeHuman}</p>
                </div>
                <div>
                  <label className="block text-sm text-gray-400 mb-1">Triangles</label>
                  <p className="text-white font-mono">{fileInfo?.triangles.toLocaleString()}</p>
                </div>
                <div>
                  <label className="block text-sm text-gray-400 mb-1">Format</label>
                  <p className="text-white font-mono capitalize">{fileInfo?.format}</p>
                </div>
              </div>
            </div>

            {/* 3D Viewer */}
            <div className="bg-gradient-to-b from-gray-800 to-gray-900 rounded-lg overflow-hidden border border-gray-700" style={{ height: '500px' }}>
              <STLViewer file={file} autoRotate={true} cameraPosition={[0, 0, 150]} />
            </div>

            {/* Controls */}
            <div className="bg-gray-800 rounded-lg p-6 border border-gray-700">
              <h3 className="text-lg font-semibold text-white mb-4">Controls</h3>
              <div className="grid grid-cols-2 md:grid-cols-4 gap-4 text-sm">
                <div className="bg-gray-900 p-3 rounded">
                  <p className="text-blue-400 font-semibold">Left Click + Drag</p>
                  <p className="text-gray-300">Rotate model</p>
                </div>
                <div className="bg-gray-900 p-3 rounded">
                  <p className="text-blue-400 font-semibold">Right Click + Drag</p>
                  <p className="text-gray-300">Pan model</p>
                </div>
                <div className="bg-gray-900 p-3 rounded">
                  <p className="text-blue-400 font-semibold">Mouse Wheel</p>
                  <p className="text-gray-300">Zoom in/out</p>
                </div>
                <div className="bg-gray-900 p-3 rounded">
                  <p className="text-blue-400 font-semibold">Double Click</p>
                  <p className="text-gray-300">Reset view</p>
                </div>
              </div>
            </div>

            {/* Action Buttons */}
            <div className="flex gap-4 justify-center">
              <button
                onClick={() => setShowModal(true)}
                className="px-6 py-3 bg-blue-600 hover:bg-blue-700 text-white font-semibold rounded-lg transition-colors"
              >
                Open in Modal
              </button>
              <button
                onClick={clearFile}
                className="px-6 py-3 bg-gray-700 hover:bg-gray-600 text-white font-semibold rounded-lg transition-colors"
              >
                Upload Different File
              </button>
            </div>
          </div>
        )}

        {/* Empty State */}
        {!file && !isLoading && errors.length === 0 && (
          <div className="bg-gray-800 rounded-lg p-12 text-center border border-gray-700">
            <svg
              className="mx-auto h-16 w-16 text-gray-600 mb-4"
              fill="none"
              stroke="currentColor"
              viewBox="0 0 24 24"
            >
              <path
                strokeLinecap="round"
                strokeLinejoin="round"
                strokeWidth={1.5}
                d="M20.354 15.354A9 9 0 015.646 5.646 9 9 0 0120.354 15.354z"
              />
            </svg>
            <h3 className="text-xl font-semibold text-gray-300 mb-2">No File Selected</h3>
            <p className="text-gray-400">Upload an STL file to get started</p>
          </div>
        )}
      </div>

      {/* Preview Modal */}
      <STLPreviewModal isOpen={showModal} file={file} fileName={fileInfo?.name} onClose={() => setShowModal(false)} />
    </div>
  );
};

export default STLViewerDemo;
