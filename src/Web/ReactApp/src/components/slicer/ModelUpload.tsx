import axios from 'axios';
import React, { useCallback, useState } from 'react';

export default function ModelUpload({ onUploaded }: { onUploaded?: (id: string) => void }) {
  const [dragOver, setDragOver] = useState(false);
  const [progress, setProgress] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);

  const onDrop = useCallback(async (e: React.DragEvent) => {
    e.preventDefault();
    setDragOver(false);
    setError(null);
    const files = e.dataTransfer.files;
    if (!files || files.length === 0) return;
    await uploadFile(files[0]);
  }, []);

  const onSelect = useCallback(async (e: React.ChangeEvent<HTMLInputElement>) => {
    setError(null);
    const files = e.target.files;
    if (!files || files.length === 0) return;
    await uploadFile(files[0]);
  }, []);

  const uploadFile = async (file: File) => {
    try {
      setProgress(0);
      const form = new FormData();
      form.append('file', file, file.name);
      const resp = await axios.post('/api/models', form, {
        headers: { 'Content-Type': 'multipart/form-data' },
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
      // Hybrid: preserve main's richer response-based error resolution while remaining resilient
      let message = "Upload failed";
      try
      {
        const anyErr = err as any;
        if (anyErr?.response?.data != null) message = anyErr.response.data.ToString ? anyErr.response.data.ToString() : JSON.stringify(anyErr.response.data);
        else if (anyErr?.message) message = anyErr.message;
      }
      catch
      {
        // fallback
        message = String(err ?? "Upload failed");
      }
      setError(message);
      setProgress(null);
    }
  };

  return (
    <div>
      <div
        onDrop={onDrop}
        onDragOver={(e) => { e.preventDefault(); setDragOver(true); }}
        onDragLeave={() => setDragOver(false)}
        className={`border-2 rounded-md p-6 text-center ${dragOver ? 'border-blue-400 bg-blue-50' : 'border-dashed'}`}>
        <p className="mb-2">Drag & drop model file here (.stl, .3mf, .obj, .ply, .step)</p>
        <p className="text-sm text-gray-500">Or click to select a file</p>
        <input type="file" accept=".stl,.3mf,.obj,.ply,.step" onChange={onSelect} className="mt-4" />
        {progress !== null && (
          <div className="mt-4">
            <div className="text-sm">Uploading: {progress}%</div>
            <div className="w-full bg-gray-200 h-2 rounded mt-1">
              <div className="bg-blue-600 h-2 rounded" style={{ width: `${progress}%` }} />
            </div>
          </div>
        )}
        {error && <div className="mt-3 text-red-600">Error: {error}</div>}
      </div>
    </div>
  );
}
