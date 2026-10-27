import { FileThumbnail } from '@/features/files/components/FileThumbnail';
import { useInViewOnce } from '@/features/files/hooks/useInViewOnce';
import { canPreviewSource, usePrinterFilePreview } from '@/features/files/hooks/usePrinterFilePreview';
import { fileKindFromName } from '@/features/files/utils/fileDisplay';
import type { PrinterFileCatalogEntry, PrinterFileCatalogSource } from '@/types/api';

interface PrinterFileThumbnailProps {
  entry: PrinterFileCatalogEntry;
  source: PrinterFileCatalogSource | undefined;
  size: 'row' | 'card';
}

/**
 * Card/row thumbnail for a printer-resident file. The preview is requested only once the
 * item scrolls into view, and only when the source supports previews and the caller may
 * read them; otherwise the file-type glyph is shown without any request.
 */
export function PrinterFileThumbnail({ entry, source, size }: PrinterFileThumbnailProps) {
  const [ref, inView] = useInViewOnce<HTMLDivElement>();
  const enabled = inView && canPreviewSource(source) && entry.availability === 'Available';
  const { thumbnail, unavailableReason, thumbnailCollecting, isLoading } = usePrinterFilePreview(entry, enabled);
  return (
    <div ref={ref} data-testid="printer-file-thumbnail" className={size === 'card' ? 'w-full' : 'inline-flex'}>
      <FileThumbnail
        kind={fileKindFromName(entry.displayName)}
        size={size}
        blob={thumbnail}
        unavailableReason={unavailableReason}
        collecting={thumbnailCollecting}
        loading={isLoading}
        className={size === 'card' ? 'rounded-none' : undefined}
      />
    </div>
  );
}