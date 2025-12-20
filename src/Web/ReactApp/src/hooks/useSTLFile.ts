import { useState, useCallback } from 'react';
import { validateSTLFile, getSTLFileInfo } from '../utils/stlFileUtils';

interface STLFileInfo {
  name: string;
  size: number;
  sizeHuman: string;
  triangles: number;
  format: 'binary' | 'ascii' | 'unknown';
}

interface UseSTLFileReturn {
  file: File | null;
  fileInfo: STLFileInfo | null;
  errors: string[];
  isLoading: boolean;
  selectFile: (file: File) => Promise<void>;
  clearFile: () => void;
}

/**
 * Hook for managing STL file selection and validation
 */
export function useSTLFile(maxSizeMB: number = 50): UseSTLFileReturn {
  const [file, setFile] = useState<File | null>(null);
  const [fileInfo, setFileInfo] = useState<STLFileInfo | null>(null);
  const [errors, setErrors] = useState<string[]>([]);
  const [isLoading, setIsLoading] = useState(false);

  const selectFile = useCallback(
    async (selectedFile: File) => {
      setIsLoading(true);
      setErrors([]);
      setFileInfo(null);

      try {
        // Validate the file
        const validation = await validateSTLFile(selectedFile, {
          maxSizeMB,
          checkValidity: true,
        });

        if (!validation.valid) {
          setErrors(validation.errors);
          setFile(null);
          return;
        }

        // Get file info
        const info = await getSTLFileInfo(selectedFile);
        setFileInfo(info);
        setFile(selectedFile);
      } catch (error) {
        setErrors([`Error processing file: ${error instanceof Error ? error.message : String(error)}`]);
        setFile(null);
      } finally {
        setIsLoading(false);
      }
    },
    [maxSizeMB]
  );

  const clearFile = useCallback(() => {
    setFile(null);
    setFileInfo(null);
    setErrors([]);
  }, []);

  return {
    file,
    fileInfo,
    errors,
    isLoading,
    selectFile,
    clearFile,
  };
}
