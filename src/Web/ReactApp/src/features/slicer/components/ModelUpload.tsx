import { apiClient } from '@/services/api';
import React, { useCallback, useState, useRef, useEffect } from 'react';
import { FileUpload } from '@/common/components/ui/FileUpload';

const ProgressBar: React.FC<{ percent: number }> = ({ percent }) => {
  const ref = useRef<HTMLDivElement | null>(null);
  useEffect(() => {
    if (ref.current) ref.current.style.width = `${Math.min(percent, 100)}%`;
  }, [percent]);
  return <div ref={ref} className="bg-pf-accent-bg h-2 rounded-sm transition-all duration-300" />;
};

export default function ModelUpload({ onUploaded }: { onUploaded?: (id: string) => void }) {
  const [dragOver, setDragOver] = useState(false);
  const [progress, setProgress] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);

  const uploadFile = useCallback(async (file: File) => {
    try {
      setProgress(0);
      const data = await apiClient.uploadModel(file, (evt) => {
        if (evt.total) {
          setProgress(Math.round((evt.loaded / evt.total) * 100));
        }
      });
      if (onUploaded && (data as unknown as { id?: string })?.id) onUploaded(((data as unknown as { id?: string })?.id) || '');
      setProgress(null);
      setError(null);
    } catch (err: unknown) {
      const message = err instanceof Error ? err.message : String(err);
      setError(message);
    }
  }, [onUploaded]);

  const onDrop = useCallback(async (e: React.DragEvent) => {
    e.preventDefault();
    setDragOver(false);
    setError(null);
    const files = e.dataTransfer.files;
    if (!files || files.length === 0) return;
    await uploadFile(files[0]);
  }, [uploadFile]);

  const onSelect = useCallback(async (e: React.ChangeEvent<HTMLInputElement>) => {
    setError(null);
    const files = e.target.files;
    if (!files || files.length === 0) return;
    await uploadFile(files[0]);
  }, [uploadFile]);

  return (
    <div>
      <div
        onDrop={onDrop}
        onDragOver={(e) => { e.preventDefault(); setDragOver(true); }}
        onDragLeave={() => setDragOver(false)}
        className={`border-2 rounded-md p-6 text-center ${dragOver ? 'border-pf-accent bg-pf-accent-bg/15' : 'border-dashed'}`}>
        <p className="mb-2">Drag & drop model file here (.stl, .3mf, .obj, .ply, .step)</p>
        <p className="text-sm text-pf-text-secondary">Or click to select a file</p>
        <FileUpload
          id="model-upload-input"
          accept=".stl,.3mf,.obj,.ply,.step"
          onChange={(files) => {
            if (files) {
              const event = { target: { files } } as unknown as React.ChangeEvent<HTMLInputElement>;
              onSelect(event);
            }
          }}
          className="mt-4"
        />
        {progress !== null && (
          <div className="mt-4">
            <div className="text-sm">Uploading: {progress}%</div>
            <div className="w-full bg-pf-bg-2 h-2 rounded-sm mt-1">
              <ProgressBar percent={progress} />
            </div>
          </div>
        )}
        {error && <div className="mt-3 text-pf-error">Error: {error}</div>}
      </div>
    </div>
  );
}
