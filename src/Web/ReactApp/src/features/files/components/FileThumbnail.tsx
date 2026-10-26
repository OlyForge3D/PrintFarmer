import { useCallback } from 'react';
import clsx from 'clsx';
import { ImageOffIcon } from '@/common/components/icons/MdiIcons';
import { FileTypeGlyph } from '@/features/files/components/FileGlyphs';
import type { FileKind } from '@/features/files/utils/fileDisplay';

export type FileThumbnailSize = 'row' | 'card' | 'panel';

interface FileThumbnailProps {
  kind: FileKind;
  size: FileThumbnailSize;
  /** Server-hosted image (local library thumbnails). */
  src?: string | null;
  /** Fetched image bytes (printer previews). Rendered through a revocable object URL. */
  blob?: Blob | null;
  /** Set once the server reported that no preview image can be shown. */
  unavailableReason?: string | null;
  loading?: boolean;
  /** Empty for list thumbnails: the adjacent filename already names the item. */
  alt?: string;
  className?: string;
}

const FRAME_CLASS: Record<FileThumbnailSize, string> = {
  row: 'h-10 w-10',
  card: 'aspect-square w-full',
  panel: 'aspect-4/3 w-full',
};
const GLYPH_CLASS: Record<FileThumbnailSize, string> = {
  row: 'h-5 w-5',
  card: 'h-10 w-10',
  panel: 'h-16 w-16',
};

interface BlobImageProps {
  blob: Blob;
  alt: string;
  className?: string;
}

/**
 * The object URL lives exactly as long as the `<img>` node / blob pairing: the React 19 ref
 * cleanup revokes it on unmount or when the blob changes, so nothing leaks under StrictMode.
 */
function BlobImage({ blob, alt, className }: BlobImageProps) {
  const attach = useCallback((image: HTMLImageElement | null) => {
    if (!image || typeof URL.createObjectURL !== 'function') return undefined;
    const url = URL.createObjectURL(blob);
    image.src = url;
    return () => {
      image.removeAttribute('src');
      URL.revokeObjectURL(url);
    };
  }, [blob]);
  return <img ref={attach} alt={alt} className={className} />;
}

/** Shared thumbnail frame: image, explicit "Preview unavailable" state, or the file-type glyph. */
export function FileThumbnail({
  kind, size, src, blob, unavailableReason, loading = false, alt = '', className,
}: FileThumbnailProps) {
  const imageClass = 'h-full w-full object-contain';
  let content;
  if (blob) {
    content = <BlobImage blob={blob} alt={alt} className={imageClass} />;
  } else if (src) {
    content = <img src={src} alt={alt} loading="lazy" className={imageClass} />;
  } else if (unavailableReason) {
    const label = `Preview unavailable: ${unavailableReason}`;
    content = size === 'panel' ? (
      <div className="flex flex-col items-center gap-2 px-4 text-center text-pf-text-secondary">
        <span aria-hidden="true"><ImageOffIcon className="h-12 w-12" ariaLabel="" /></span>
        <p className="text-sm font-medium">Preview unavailable</p>
        <p className="text-xs">{unavailableReason}</p>
      </div>
    ) : (
      <span title={label} className="inline-flex text-pf-text-tertiary">
        <ImageOffIcon className={GLYPH_CLASS[size]} ariaLabel={label} />
      </span>
    );
  } else {
    // Placeholder only: the filename and Type column/row already convey the kind.
    content = (
      <span className={clsx('inline-flex text-pf-text-secondary', loading && 'animate-pulse')}>
        <FileTypeGlyph kind={kind} className={clsx(GLYPH_CLASS[size], 'opacity-70')} decorative />
      </span>
    );
  }
  return (
    <div
      className={clsx(
        'flex shrink-0 items-center justify-center overflow-hidden bg-pf-bg-1',
        size === 'row' ? 'rounded-xs' : 'rounded-sm',
        size !== 'row' && 'p-1',
        FRAME_CLASS[size],
        className,
      )}
      aria-busy={loading || undefined}
    >
      {content}
    </div>
  );
}