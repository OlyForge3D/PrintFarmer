import React, { useState, useEffect } from 'react';
import { Button } from '@/common/components/ui/Button';
import { CloseIcon } from '@/common/components/icons/MdiIcons';
import { STLViewer } from './STLViewer';

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
    let mounted = true;
    const controller = new AbortController();

    if (file) {
      const getFileInfo = async () => {
        try {
          // Some test environments provide a File-like object without arrayBuffer();
          // gracefully fall back to a minimal info object to avoid throwing.
          if (typeof (file as any).arrayBuffer === 'function') {
            const arrayBuffer = await (file as any).arrayBuffer();
            if (!mounted) return;
            const view = new DataView(arrayBuffer);
            const triangles = view.getUint32(80, true);
            const vertices = triangles * 3;
            const fileSize = (file.size / 1024 / 1024).toFixed(2);

            if (mounted) {
              setModelInfo({
                vertices,
                triangles,
                fileSize: `${fileSize} MB`,
                format: file.name.endsWith('.stl') ? 'STL' : 'Unknown',
              });
            }
          } else {
            // Fallback: provide basic metadata when arrayBuffer is not available
            const fileSize = (file.size / 1024 / 1024).toFixed(2);
            if (mounted) {
              setModelInfo({
                vertices: 0,
                triangles: 0,
                fileSize: `${fileSize} MB`,
                format: file.name?.endsWith('.stl') ? 'STL' : 'Unknown',
              });
            }
          }
        } catch (error) {
          console.error('Error reading file info:', error);
        }
      };

      getFileInfo();
    } else if (fileUrl && fileName) {
      // For URL-based files, estimate info from file name
      setIsLoading(true);
      fetch(fileUrl, { signal: controller.signal })
        .then(res => res.blob())
        .then(blob => {
          if (!mounted) return;
          const fileSize = (blob.size / 1024 / 1024).toFixed(2);
          setModelInfo({
            vertices: 0,
            triangles: 0,
            fileSize: `${fileSize} MB`,
            format: fileName.endsWith('.stl') ? 'STL' : fileName.split('.').pop()?.toUpperCase() || 'Unknown',
          });
          if (mounted) setIsLoading(false);
        })
        .catch(error => {
          if (error.name === 'AbortError') return; // cancelled
          console.error('Error fetching file info:', error);
          if (mounted) setIsLoading(false);
        });
    }

    return () => {
      mounted = false;
      controller.abort();
    };
  }, [file, fileUrl, fileName]);

  if (!isOpen || (!file && !fileUrl)) {
    return null;
  }

  const displayFileName = fileName || file?.name || 'Unnamed Model';
  const shouldShowViewer = file || fileUrl;

  return (
    <div 
      className="fixed inset-0 z-50 flex items-center justify-center p-4 bg-black/50 backdrop-blur-sm"
      onClick={(e) => {
        if (e.target === e.currentTarget) {
          onClose();
        }
      }}
      onKeyDown={(e) => {
        if (e.key === 'Escape') {
          onClose();
        }
      }}
    >
      <div className="rounded-lg shadow-2xl w-full max-w-4xl max-h-[90vh] overflow-hidden flex flex-col bg-pf-bg-1 border border-pf-border">
        {/* Header */}
        <div className="px-6 py-4 flex items-center justify-between border-b border-pf-border" style={{
          background: 'linear-gradient(to right, var(--pf-bg-0), var(--pf-bg-1))',
        }}>
          <div>
            <h2 className="text-xl font-bold text-pf-text-primary">STL Model Preview</h2>
            <p className="text-sm mt-1 text-pf-text-secondary">{displayFileName}</p>
          </div>
          <Button variant="subtle" size="sm" onClick={onClose} className="p-1">
            <CloseIcon className="w-5 h-5" />
          </Button>
        </div>

        {/* Content */}
        <div className="flex-1 flex gap-4 p-4 min-h-0">
          {/* 3D Viewer */}
          <div className="flex-1 rounded-lg overflow-hidden border border-pf-border" style={{
            background: 'linear-gradient(to bottom, var(--pf-bg-0), var(--pf-bg-1))',
          }}>
            {shouldShowViewer && (
              file ? (
                <STLViewer file={file} autoRotate={false} cameraPosition={[0, 0, 150]} />
              ) : fileUrl ? (
                <STLViewer file={fileUrl} autoRotate={false} cameraPosition={[0, 0, 150]} />
              ) : null
            )}
          </div>

          {/* Info Panel */}
          <div className="w-64 rounded-lg p-4 overflow-y-auto bg-pf-bg-1 border border-pf-border">
            <h3 className="text-lg font-semibold mb-4 text-pf-text-primary">Model Information</h3>

            {modelInfo ? (
              <div className="space-y-4">
                {/* File Size */}
                <div>
                  <label className="block text-sm mb-1 text-pf-text-secondary">File Size</label>
                  <p className="text-lg font-mono text-pf-text-primary">{modelInfo.fileSize}</p>
                </div>

                {/* Triangle Count */}
                {modelInfo.triangles > 0 && (
                  <div>
                    <label className="block text-sm mb-1 text-pf-text-secondary">Triangles</label>
                    <p className="text-lg font-mono text-pf-text-primary">{modelInfo.triangles.toLocaleString()}</p>
                  </div>
                )}

                {/* Vertex Count */}
                {modelInfo.vertices > 0 && (
                  <div>
                    <label className="block text-sm mb-1 text-pf-text-secondary">Vertices</label>
                    <p className="text-lg font-mono text-pf-text-primary">{modelInfo.vertices.toLocaleString()}</p>
                  </div>
                )}

                {/* Format */}
                <div>
                  <label className="block text-sm mb-1 text-pf-text-secondary">Format</label>
                  <p className="text-lg font-mono text-pf-text-primary">{modelInfo.format}</p>
                </div>

                {/* Separator */}
                <div className="border-t border-pf-border my-2"></div>

                {/* Controls Info */}
                <div className="bg-pf-bg-2 rounded p-3 border border-pf-border">
                  <h4 className="text-sm font-semibold text-pf-text-primary mb-2">Controls</h4>
                  <div className="text-xs text-pf-text-secondary space-y-1">
                    <p><span className="text-pf-accent">Left Click + Drag</span> - Rotate</p>
                    <p><span className="text-pf-accent">Right Click + Drag</span> - Pan</p>
                    <p><span className="text-pf-accent">Scroll</span> - Zoom</p>
                    <p><span className="text-pf-accent">Double Click</span> - Reset View</p>
                  </div>
                </div>
              </div>
            ) : isLoading ? (
              <div className="flex items-center justify-center h-full">
                <div className="text-center">
                  <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-pf-accent mx-auto"></div>
                  <p className="text-sm text-pf-text-secondary mt-2">Loading info...</p>
                </div>
              </div>
            ) : (
              <p className="text-sm text-pf-text-secondary">Unable to load model information</p>
            )}
          </div>
        </div>

        {/* Footer */}
        <div className="bg-pf-bg-1 px-6 py-3 border-t border-pf-border flex justify-end gap-3">
          <Button variant="secondary" onClick={onClose}>
            Close
          </Button>
          {onUseModel && (
            <Button variant="primary" onClick={onUseModel}>
              Use This Model
            </Button>
          )}
        </div>
      </div>
    </div>
  );
};

export default STLPreviewModal;
