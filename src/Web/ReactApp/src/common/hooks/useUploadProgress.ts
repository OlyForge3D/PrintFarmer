import { useState, useCallback, useRef } from 'react';

export interface UploadProgress {
  isUploading: boolean;
  progress: number; // 0-100
  error: string | null;
}

export interface UseUploadProgressOptions {
  onSuccess?: (data: unknown) => void;
  onError?: (error: Error) => void;
}

/**
 * Hook for managing file upload progress and status.
 * Provides isUploading, progress percentage, and error state.
 * 
 * @example
 * ```tsx
 * const { isUploading, progress, error, uploadFile, reset } = useUploadProgress();
 * 
 * const handleUpload = async (file: File) => {
 *   await uploadFile(async (onProgress) => {
 *     const formData = new FormData();
 *     formData.append('file', file);
 *     
 *     return await axios.post('/api/upload', formData, {
 *       onUploadProgress: (e) => {
 *         onProgress(Math.round((e.loaded / e.total!) * 100));
 *       }
 *     });
 *   });
 * };
 * ```
 */
export function useUploadProgress(options?: UseUploadProgressOptions) {
  const [isUploading, setIsUploading] = useState(false);
  const [progress, setProgress] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const abortControllerRef = useRef<AbortController | null>(null);

  const uploadFile = useCallback(async (
    uploadFn: (onProgress: (percent: number) => void) => Promise<unknown>
  ) => {
    try {
      setIsUploading(true);
      setError(null);
      setProgress(0);
      abortControllerRef.current = new AbortController();

      const data = await uploadFn((percent) => {
        setProgress(Math.min(percent, 99)); // Cap at 99% until complete
      });

      setProgress(100);
      options?.onSuccess?.(data);
      return data;
    } catch (err) {
      const errorMessage = err instanceof Error ? err.message : 'Upload failed';
      setError(errorMessage);
      options?.onError?.(err instanceof Error ? err : new Error(errorMessage));
      throw err;
    } finally {
      setIsUploading(false);
      setTimeout(() => {
        setProgress(0);
      }, 1000); // Clear progress after a brief delay
    }
  }, [options]);

  const cancel = useCallback(() => {
    abortControllerRef.current?.abort();
    setIsUploading(false);
    setProgress(0);
  }, []);

  const reset = useCallback(() => {
    setIsUploading(false);
    setProgress(0);
    setError(null);
  }, []);

  return {
    isUploading,
    progress,
    error,
    uploadFile,
    cancel,
    reset,
  };
}
