import {
  sliceJobService,
  type ArtifactListItemResponse,
  type PromoteSliceArtifactResponse,
} from '@/services/sliceJobService';
import {
  isGcodeFileName,
  isTextGcodeFileName,
} from '@/features/slicer/utils/gcodeFileUtils';

async function findGcodeArtifact(
  artifactsRoute: string,
  isSupported: (fileName: string) => boolean,
): Promise<ArtifactListItemResponse | null> {
  const artifacts = await sliceJobService.getArtifactsByRoute(artifactsRoute);
  return artifacts.find(artifact => isSupported(artifact.fileName)) ?? null;
}

export async function resolveGcodeArtifact(
  artifactsRoute: string,
): Promise<ArtifactListItemResponse | null> {
  return findGcodeArtifact(artifactsRoute, isTextGcodeFileName);
}

export async function resolveGcodeArtifactForAction(
  artifactsRoute: string,
): Promise<ArtifactListItemResponse | null> {
  return findGcodeArtifact(artifactsRoute, isGcodeFileName);
}

export async function downloadGcodeArtifact(artifactsRoute: string): Promise<void> {
  const artifact = await resolveGcodeArtifactForAction(artifactsRoute);
  if (!artifact) {
    throw new Error('No G-code artifact is available for this job.');
  }

  const blob = await sliceJobService.downloadArtifact(artifact.id);
  const objectUrl = URL.createObjectURL(blob);
  try {
    const link = document.createElement('a');
    link.href = objectUrl;
    link.download = artifact.fileName;
    document.body.appendChild(link);
    link.click();
    link.remove();
  } finally {
    URL.revokeObjectURL(objectUrl);
  }
}

export async function saveGcodeArtifactToLibrary(
  artifactsRoute: string,
  sliceJobId: string,
): Promise<PromoteSliceArtifactResponse> {
  const artifact = await resolveGcodeArtifactForAction(artifactsRoute);
  if (!artifact) {
    throw new Error('No G-code artifact is available for this job.');
  }

  return sliceJobService.promoteSliceArtifact(sliceJobId, artifact.id);
}
