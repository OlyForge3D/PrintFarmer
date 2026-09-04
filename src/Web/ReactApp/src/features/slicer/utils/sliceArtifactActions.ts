import {
  sliceJobService,
  type ArtifactListItemResponse,
  type PromoteSliceArtifactResponse,
} from '@/services/sliceJobService';
import {
  isGcodeFileName,
  isTextGcodeFileName,
} from '@/features/slicer/utils/gcodeFileUtils';

export type GcodeArtifactSelection =
  | { status: 'selected'; artifact: ArtifactListItemResponse }
  | { status: 'unavailable'; message: string }
  | { status: 'selection-required'; message: string };

export function selectGcodeArtifact(
  artifacts: ArtifactListItemResponse[],
): GcodeArtifactSelection {
  const gcodeArtifacts = artifacts.filter(artifact => isGcodeFileName(artifact.fileName));
  if (gcodeArtifacts.length === 0) {
    return {
      status: 'unavailable',
      message: 'No G-code artifact is available for this job.',
    };
  }

  if (gcodeArtifacts.length === 1) {
    return { status: 'selected', artifact: gcodeArtifacts[0] };
  }

  const primaryArtifacts = gcodeArtifacts.filter(artifact => artifact.isPrimary);
  if (primaryArtifacts.length !== 1) {
    return {
      status: 'selection-required',
      message: 'Multiple G-code artifacts are available, but the server did not declare exactly one valid primary artifact.',
    };
  }

  return { status: 'selected', artifact: primaryArtifacts[0] };
}

async function resolveGcodeSelection(
  artifactsRoute: string,
): Promise<GcodeArtifactSelection> {
  const artifacts = await sliceJobService.getArtifactsByRoute(artifactsRoute);
  return selectGcodeArtifact(artifacts);
}

export async function resolveGcodeArtifact(
  artifactsRoute: string,
): Promise<ArtifactListItemResponse | null> {
  const artifact = await resolveGcodeArtifactForAction(artifactsRoute);
  return isTextGcodeFileName(artifact.fileName) ? artifact : null;
}

export async function resolveGcodeArtifactForAction(
  artifactsRoute: string,
): Promise<ArtifactListItemResponse> {
  const selection = await resolveGcodeSelection(artifactsRoute);
  if (selection.status !== 'selected') {
    throw new Error(selection.message);
  }
  return selection.artifact;
}

export async function downloadGcodeArtifact(artifactsRoute: string): Promise<void> {
  const artifact = await resolveGcodeArtifactForAction(artifactsRoute);

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
  return sliceJobService.promoteSliceArtifact(sliceJobId, artifact.id);
}
