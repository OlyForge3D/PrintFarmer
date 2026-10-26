import type { ComponentType } from 'react';
import clsx from 'clsx';
import {
  AlertCircleIcon,
  AlertIcon,
  CheckCircleIcon,
  CopyLocalIcon,
  CubeIcon,
  FileIcon,
  GcodeFileIcon,
  HistoryIcon,
} from '@/common/components/icons/MdiIcons';
import type { PrinterFileCatalogEntry } from '@/types/api';
import { FILE_KIND_LABEL, type FileKind } from '@/features/files/utils/fileDisplay';

type GlyphIcon = ComponentType<{ className?: string; ariaLabel?: string }>;

const KIND_ICON: Record<FileKind, GlyphIcon> = {
  gcode: GcodeFileIcon,
  model: CubeIcon,
  other: FileIcon,
};

interface FileTypeGlyphProps {
  kind: FileKind;
  className?: string;
  /** Hide from assistive tech when adjacent text already names the type. */
  decorative?: boolean;
}

/** One glyph per file kind, shared by cards, tables and the detail panel of both sources. */
export function FileTypeGlyph({ kind, className = 'h-4 w-4', decorative = false }: FileTypeGlyphProps) {
  const Icon = KIND_ICON[kind];
  if (decorative) {
    return <span aria-hidden="true" className="inline-flex shrink-0"><Icon className={className} ariaLabel="" /></span>;
  }
  return <Icon className={clsx('shrink-0', className)} ariaLabel={FILE_KIND_LABEL[kind]} />;
}

interface StatusDescriptor {
  key: string;
  label: string;
  tone: string;
  Icon: GlyphIcon;
}

function printerFileStatuses(entry: PrinterFileCatalogEntry): StatusDescriptor[] {
  const statuses: StatusDescriptor[] = [];
  if (entry.availability === 'Available') {
    statuses.push({ key: 'availability', label: 'Available', tone: 'text-pf-success', Icon: CheckCircleIcon });
  } else if (entry.availability === 'Missing') {
    statuses.push({ key: 'availability', label: 'Missing', tone: 'text-pf-warning', Icon: AlertCircleIcon });
  } else {
    statuses.push({ key: 'availability', label: 'Deleted', tone: 'text-pf-error', Icon: AlertIcon });
  }
  if (entry.isStale) {
    statuses.push({ key: 'stale', label: 'Stale', tone: 'text-pf-warning', Icon: HistoryIcon });
  }
  if (entry.localCopies.length > 0) {
    statuses.push({ key: 'local', label: 'Local copy captured', tone: 'text-pf-accent', Icon: CopyLocalIcon });
  }
  return statuses;
}

interface PrinterFileStatusGlyphsProps {
  entry: PrinterFileCatalogEntry;
  /** Tables show glyph + text; cards show glyphs only (each still named and titled). */
  showLabels?: boolean;
  className?: string;
}

/** Availability / freshness / captured-copy glyphs shared by printer cards, rows and panel. */
export function PrinterFileStatusGlyphs({ entry, showLabels = false, className }: PrinterFileStatusGlyphsProps) {
  const statuses = printerFileStatuses(entry);
  if (showLabels) {
    return (
      <ul className={clsx('flex flex-wrap gap-x-2 gap-y-0.5', className)}>
        {statuses.map(({ key, label, tone, Icon }) => (
          <li key={key} className="inline-flex items-center gap-1">
            <span aria-hidden="true" className={clsx('inline-flex', tone)}><Icon className="h-3.5 w-3.5" ariaLabel="" /></span>
            {label}
          </li>
        ))}
      </ul>
    );
  }
  return (
    <span className={clsx('inline-flex items-center gap-0.5', className)}>
      {statuses.map(({ key, label, tone, Icon }) => (
        <span key={key} title={label} className={clsx('inline-flex', tone)}>
          <Icon className="h-3.5 w-3.5" ariaLabel={label} />
        </span>
      ))}
    </span>
  );
}