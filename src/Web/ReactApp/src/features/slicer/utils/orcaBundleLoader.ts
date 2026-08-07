export async function readOrcaBundle(buffer: ArrayBuffer): Promise<string | null> {
  const { extractOrcaBundle, isZipFile } = await import(
    '@/features/slicer/orca/utils/orcaBundleExtractor'
  );

  return isZipFile(buffer) ? extractOrcaBundle(buffer) : null;
}
