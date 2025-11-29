import axios from 'axios';
import React, { useCallback, useState, useRef, useEffect } from 'react';
import { getApiBaseUrl } from '@/utils/apiUrlHelpers';
import { FileUpload } from '@/components/ui/FileUpload';

const ProgressBar: React.FC<{ percent: number }> = ({ percent }) => {
  const ref = useRef<HTMLDivElement | null>(null);
  useEffect(() => {
    if (ref.current) ref.current.style.width = `${Math.min(percent, 100)}%`;
  }, [percent]);
  return <div ref={ref} className="bg-blue-600 h-2 rounded transition-all duration-300" />;
};

export default function ModelUpload({ onUploaded }: { onUploaded?: (id: string) => void }) {
  const [dragOver, setDragOver] = useState(false);
  const [progress, setProgress] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);

  const uploadFile = useCallback(async (file: File) => {
    try {
      setProgress(0);
      const form = new FormData();
      form.append('file', file, file.name);
      const token = localStorage.getItem('auth-token');
      const headers: Record<string, string> = { 'Content-Type': 'multipart/form-data' };
      if (token) {
        headers['Authorization'] = `Bearer ${token}`;
      }
      const resp = await axios.post(`${getApiBaseUrl()}/models`, form, {
        headers,
        onUploadProgress: (evt) => {
          if (evt.total) {
            setProgress(Math.round((evt.loaded / evt.total) * 100));
          }
        }
      });

      const data = resp.data;
      if (onUploaded && data?.id) onUploaded(data.id);
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
        className={`border-2 rounded-md p-6 text-center ${dragOver ? 'border-blue-400 bg-blue-50' : 'border-dashed'}`}>
        <p className="mb-2">Drag & drop model file here (.stl, .3mf, .obj, .ply, .step)</p>
        <p className="text-sm text-gray-500">Or click to select a file</p>
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
            <div className="w-full bg-gray-200 h-2 rounded mt-1">
              <ProgressBar percent={progress} />
            </div>
          </div>
        )}
        {error && <div className="mt-3 text-red-600">Error: {error}</div>}
      </div>
    </div>
  );
}
