import { sliceJobService, type ArtifactListItemResponse } from '@/services/sliceJobService';
import { isGcodeFileName } from '@/features/slicer/utils/gcodeFileUtils';

export async function resolveGcodeArtifact(
  artifactsRoute: string,
): Promise<ArtifactListItemResponse | null> {
  const artifacts = await sliceJobService.getArtifactsByRoute(artifactsRoute);
  return artifacts.find(artifact => isGcodeFileName(artifact.fileName)) ?? null;
}

export async function downloadGcodeArtifact(artifactsRoute: string): Promise<void> {
  const artifact = await resolveGcodeArtifact(artifactsRoute);
  if (!artifact) {
    throw new Error('No G-code artifact is available for this job.');
  }

  const blob = await sliceJobService.downloadArtifact(artifact.id);
  const objectUrl = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = objectUrl;
  link.download = artifact.fileName;
  document.body.appendChild(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(objectUrl);
}
