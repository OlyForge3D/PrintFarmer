/**
 * STL File Utilities
 * Helper functions for STL file validation and processing
 */

/**
 * Validates if a file is a valid STL file
 * Checks both ASCII and binary STL formats
 */
export async function isValidSTLFile(file: File): Promise<boolean> {
  try {
    const arrayBuffer = await file.arrayBuffer();
    const view = new Uint8Array(arrayBuffer);

    // Check for ASCII STL (starts with "solid")
    if (view.length > 5) {
      const header = new TextDecoder().decode(view.slice(0, 5));
      if (header === 'solid') {
        return true;
      }
    }

    // Check for binary STL (has proper header and triangle count)
    if (view.length > 84) {
      const dataView = new DataView(arrayBuffer);
      const triangles = dataView.getUint32(80, true);
      const expectedSize = 84 + triangles * 50; // 80 byte header + 4 byte count + (triangles * 50 bytes each)
      
      // Allow some tolerance for the file size check
      const sizeTolerance = expectedSize * 0.1;
      if (arrayBuffer.byteLength >= expectedSize - sizeTolerance) {
        return true;
      }
    }

    return false;
  } catch (error) {
    console.error('Error validating STL file:', error);
    return false;
  }
}

/**
 * Gets information about an STL file
 */
export async function getSTLFileInfo(file: File): Promise<{
  name: string;
  size: number;
  sizeHuman: string;
  triangles: number;
  format: 'binary' | 'ascii' | 'unknown';
}> {
  try {
    const arrayBuffer = await file.arrayBuffer();
    const view = new Uint8Array(arrayBuffer);
    const dataView = new DataView(arrayBuffer);

    let format: 'binary' | 'ascii' | 'unknown' = 'unknown';
    let triangles = 0;

    // Check for ASCII STL
    if (view.length > 5) {
      const header = new TextDecoder().decode(view.slice(0, 5));
      if (header === 'solid') {
        format = 'ascii';
        // Count triangles in ASCII file
        const text = new TextDecoder().decode(arrayBuffer);
        const matches = text.match(/facet normal/g);
        triangles = matches ? matches.length : 0;
      }
    }

    // Check for binary STL
    if (format === 'unknown' && view.length > 84) {
      format = 'binary';
      triangles = dataView.getUint32(80, true);
    }

    const sizeMB = (file.size / 1024 / 1024).toFixed(2);
    const sizeHuman = file.size > 1024 * 1024
      ? `${sizeMB} MB`
      : `${(file.size / 1024).toFixed(2)} KB`;

    return {
      name: file.name,
      size: file.size,
      sizeHuman,
      triangles,
      format,
    };
  } catch (error) {
    console.error('Error getting STL file info:', error);
    return {
      name: file.name,
      size: file.size,
      sizeHuman: `${(file.size / 1024).toFixed(2)} KB`,
      triangles: 0,
      format: 'unknown',
    };
  }
}

/**
 * Validates file size (default max 50MB)
 */
export function validateSTLFileSize(file: File, maxSizeMB: number = 50): {
  valid: boolean;
  error?: string;
} {
  const maxSizeBytes = maxSizeMB * 1024 * 1024;
  
  if (file.size > maxSizeBytes) {
    return {
      valid: false,
      error: `File is too large (${(file.size / 1024 / 1024).toFixed(2)} MB). Maximum size is ${maxSizeMB} MB.`,
    };
  }

  return { valid: true };
}

/**
 * Validates multiple criteria for STL file acceptance
 */
export async function validateSTLFile(
  file: File,
  options: {
    maxSizeMB?: number;
    checkValidity?: boolean;
  } = {}
): Promise<{
  valid: boolean;
  errors: string[];
}> {
  const errors: string[] = [];

  // Check file extension
  if (!file.name.toLowerCase().endsWith('.stl')) {
    errors.push('File must be an STL file (.stl extension)');
  }

  // Check file size
  const sizeValidation = validateSTLFileSize(file, options.maxSizeMB);
  if (!sizeValidation.valid && sizeValidation.error) {
    errors.push(sizeValidation.error);
  }

  // Check file validity
  if (options.checkValidity !== false) {
    const isValid = await isValidSTLFile(file);
    if (!isValid) {
      errors.push('File does not appear to be a valid STL file');
    }
  }

  return {
    valid: errors.length === 0,
    errors,
  };
}

/**
 * Formats file size for display
 */
export function formatFileSize(bytes: number): string {
  if (bytes === 0) return '0 Bytes';
  
  const k = 1024;
  const sizes = ['Bytes', 'KB', 'MB', 'GB'];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  
  return Math.round((bytes / Math.pow(k, i)) * 100) / 100 + ' ' + sizes[i];
}
