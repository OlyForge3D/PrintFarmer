const DEFAULT_MODEL_EXTENSION = 'stl';
const FORCE_STL_QUERY = 'forceStl=true';
const STL_COMPATIBLE_FORMAT = '3mf';

export type SlicerViewerFileType = 'stl' | 'ply' | '3mf';

export function getModelFileExtension(fileName?: string): string {
  const normalizedFileName = fileName?.trim() ?? '';
  const lastDotIndex = normalizedFileName.lastIndexOf('.');

  if (lastDotIndex < 0 || lastDotIndex === normalizedFileName.length - 1) {
    return DEFAULT_MODEL_EXTENSION;
  }

  return normalizedFileName.slice(lastDotIndex + 1).toLowerCase();
}

export function getSlicerViewerFileType(fileName?: string): SlicerViewerFileType {
  const fileExtension = getModelFileExtension(fileName);

  if (fileExtension === 'ply' || fileExtension === STL_COMPATIBLE_FORMAT) {
    return fileExtension;
  }

  return DEFAULT_MODEL_EXTENSION;
}

export function buildSlicerViewerModelUrl(apiBase: string, modelId: string, fileName?: string): string {
  const modelUrl = `${apiBase}/3d-models/file/${modelId}`;

  if (getModelFileExtension(fileName) === STL_COMPATIBLE_FORMAT) {
    return `${modelUrl}?${FORCE_STL_QUERY}`;
  }

  return modelUrl;
}
