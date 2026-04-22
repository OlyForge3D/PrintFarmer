import { useState, useRef, useCallback } from 'react';
import QRCode from 'qrcode';
import { Button, Select } from '@/common/components/ui';
import { FormField } from '@/common/components/ui/FormField';
import { Modal } from '@/common/components/modals/Modal';
import { PrinterIcon } from '@/common/components/icons/MdiIcons';
import type { SpoolmanSpoolDto } from '@/features/filamentManagement/types';

type LabelFormat = 'small' | 'a4';

interface SpoolLabelModalProps {
  isOpen: boolean;
  onClose: () => void;
  spool: SpoolmanSpoolDto | null;
}

const LABEL_FORMATS: Record<LabelFormat, { name: string; description: string }> = {
  small: { name: 'Small (62mm)', description: 'DYMO / Brother label printers' },
  a4: { name: 'A4 Sheet (8-up)', description: '2×4 grid for laser / inkjet' },
};

function buildSpoolUrl(spoolId: number): string {
  return `${window.location.origin}/filament/spools/${spoolId}`;
}

function formatWeight(grams: number | null | undefined): string {
  if (grams == null) return '—';
  return grams >= 1000 ? `${(grams / 1000).toFixed(1)}kg` : `${Math.round(grams)}g`;
}

function isColorDark(hex: string): boolean {
  const h = hex.replace('#', '');
  const r = parseInt(h.substring(0, 2), 16);
  const g = parseInt(h.substring(2, 4), 16);
  const b = parseInt(h.substring(4, 6), 16);
  return (0.299 * r + 0.587 * g + 0.114 * b) / 255 < 0.5;
}

/** Single label layout rendered as a self-contained element. */
function SpoolLabel({ spool, qrDataUrl }: { spool: SpoolmanSpoolDto; qrDataUrl: string }) {
  const displayName = spool.filamentName || spool.name || 'Unknown';
  const vendor = spool.vendor || '';
  const colorHex = spool.colorHex || '#cccccc';
  const isDark = isColorDark(colorHex);

  return (
    <div className="spool-label" style={{ width: '62mm', height: '29mm', padding: '2mm', display: 'flex', gap: '2mm', border: '0.5pt solid #ccc', boxSizing: 'border-box', fontFamily: 'system-ui, sans-serif', overflow: 'hidden', pageBreakInside: 'avoid' }}>
      <div style={{ flexShrink: 0, display: 'flex', alignItems: 'center' }}>
        {qrDataUrl ? (
          <img src={qrDataUrl} alt={`QR code for spool #${spool.id}`} style={{ width: '22mm', height: '22mm' }} />
        ) : (
          <div style={{ width: '22mm', height: '22mm', background: '#eee' }} />
        )}
      </div>
      <div style={{ flex: 1, display: 'flex', flexDirection: 'column', justifyContent: 'space-between', minWidth: 0, overflow: 'hidden' }}>
        <div>
          <div style={{ fontSize: '9pt', fontWeight: 700, lineHeight: 1.2, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {displayName}
          </div>
          {vendor && (
            <div style={{ fontSize: '7pt', color: '#666', lineHeight: 1.2, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              {vendor}
            </div>
          )}
        </div>
        <div style={{ display: 'flex', alignItems: 'center', gap: '2mm' }}>
          <div
            style={{ width: '8mm', height: '8mm', borderRadius: '1mm', backgroundColor: colorHex, border: '0.5pt solid #999', flexShrink: 0 }}
            aria-label={`Color: ${colorHex}`}
          />
          <div style={{ minWidth: 0 }}>
            {spool.material && <div style={{ fontSize: '8pt', fontWeight: 600, lineHeight: 1.2 }}>{spool.material}</div>}
            <div style={{ fontSize: '6.5pt', color: '#888', lineHeight: 1.2 }}>{colorHex.toUpperCase()}</div>
          </div>
        </div>
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-end' }}>
          <div style={{ fontSize: '8pt', fontWeight: 600 }}>{formatWeight(spool.initialWeightG)}</div>
          <div style={{ fontSize: '7pt', color: isDark ? '#fff' : '#333', backgroundColor: colorHex, padding: '0.5mm 1.5mm', borderRadius: '0.5mm', border: '0.5pt solid #999', fontWeight: 600 }}>
            #{spool.id}
          </div>
        </div>
      </div>
    </div>
  );
}

/** A4 layout: 8 labels in a 2×4 grid. */
function A4LabelSheet({ spool, qrDataUrl }: { spool: SpoolmanSpoolDto; qrDataUrl: string }) {
  return (
    <div
      className="a4-label-sheet"
      style={{ width: '210mm', height: '297mm', padding: '10mm 15mm', display: 'grid', gridTemplateColumns: 'repeat(2, 1fr)', gridTemplateRows: 'repeat(4, auto)', gap: '4mm', boxSizing: 'border-box', pageBreakAfter: 'always' }}
    >
      {Array.from({ length: 8 }, (_, i) => (
        <SpoolLabel key={i} spool={spool} qrDataUrl={qrDataUrl} />
      ))}
    </div>
  );
}

export function SpoolLabelModal({ isOpen, onClose, spool }: SpoolLabelModalProps) {
  const [format, setFormat] = useState<LabelFormat>('small');
  const [qrDataUrl, setQrDataUrl] = useState('');
  const printRef = useRef<HTMLDivElement>(null);

  // Generate QR via ref callback when the hidden canvas mounts — avoids useEffect + setState
  const qrCanvasRef = useCallback((canvas: HTMLCanvasElement | null) => {
    if (!canvas || !spool) return;
    QRCode.toCanvas(canvas, buildSpoolUrl(spool.id), {
      width: 200,
      margin: 1,
      errorCorrectionLevel: 'M',
      color: { dark: '#000000', light: '#ffffff' },
    }).then(() => {
      setQrDataUrl(canvas.toDataURL('image/png'));
    }).catch(() => {
      setQrDataUrl('');
    });
  }, [spool]);

  const handleClose = useCallback(() => {
    setQrDataUrl('');
    onClose();
  }, [onClose]);

  const handlePrint = useCallback(() => {
    if (!printRef.current) return;
    const printContent = printRef.current.innerHTML;
    const printWindow = window.open('', '_blank', 'width=800,height=600');
    if (!printWindow) return;

    const isA4 = format === 'a4';
    const pageSize = isA4 ? 'A4' : '62mm 29mm';

    printWindow.document.write(`<!DOCTYPE html>
<html>
<head>
  <title>Spool Label #${spool?.id ?? ''}</title>
  <style>
    @page { size: ${pageSize}${isA4 ? ' portrait' : ''}; margin: 0; }
    * { box-sizing: border-box; margin: 0; padding: 0; }
    body { font-family: system-ui, -apple-system, sans-serif; }
    @media print { body { -webkit-print-color-adjust: exact; print-color-adjust: exact; } }
  </style>
</head>
<body>${printContent}</body>
</html>`);
    printWindow.document.close();

    const images = printWindow.document.querySelectorAll('img');
    const loadPromises = Array.from(images).map(img =>
      img.complete ? Promise.resolve() : new Promise<void>(resolve => {
        img.onload = () => resolve();
        img.onerror = () => resolve();
      })
    );
    Promise.all(loadPromises).then(() => {
      printWindow.focus();
      printWindow.print();
      printWindow.close();
    });
  }, [format, spool?.id]);

  if (!spool) return null;

  const displayName = spool.filamentName || spool.name || 'Unknown';

  return (
    <Modal
      isOpen={isOpen}
      onClose={handleClose}
      title={`Print Label — ${displayName} #${spool.id}`}
      width="max-w-3xl"
      closeOnEscape
      titleIcon={<PrinterIcon className="h-5 w-5" />}
      footer={
        <div className="flex gap-2 justify-end">
          <Button variant="secondary" size="sm" onClick={handleClose}>Cancel</Button>
          <Button variant="primary" size="sm" onClick={handlePrint} disabled={!qrDataUrl} iconLeft={<PrinterIcon className="h-4 w-4" />}>
            Print
          </Button>
        </div>
      }
    >
      <div className="space-y-4">
        {/* Hidden canvas for QR generation */}
        <canvas ref={qrCanvasRef} style={{ display: 'none' }} aria-hidden="true" />

        <FormField label="Label Format" htmlFor="label-format">
          <Select id="label-format" value={format} onChange={e => setFormat(e.target.value as LabelFormat)}>
            {Object.entries(LABEL_FORMATS).map(([key, { name, description }]) => (
              <option key={key} value={key}>{name} — {description}</option>
            ))}
          </Select>
        </FormField>

        {/* Live preview */}
        <div className="rounded-lg border border-pf-border bg-white p-4 overflow-auto" style={{ maxHeight: format === 'a4' ? '500px' : 'auto' }}>
          <div className="text-xs text-pf-text-secondary mb-2 font-medium">Preview</div>
          <div
            ref={printRef}
            style={{ transform: format === 'a4' ? 'scale(0.45)' : 'scale(1)', transformOrigin: 'top left', height: format === 'a4' ? '135mm' : 'auto' }}
          >
            {format === 'small' ? (
              <SpoolLabel spool={spool} qrDataUrl={qrDataUrl} />
            ) : (
              <A4LabelSheet spool={spool} qrDataUrl={qrDataUrl} />
            )}
          </div>
        </div>

        <div className="text-xs text-pf-text-secondary space-y-1">
          <div><span className="font-medium">Material:</span> {spool.material || '—'}</div>
          <div><span className="font-medium">Vendor:</span> {spool.vendor || '—'}</div>
          <div><span className="font-medium">Color:</span> {spool.colorHex?.toUpperCase() || '—'}</div>
          <div><span className="font-medium">Weight:</span> {formatWeight(spool.initialWeightG)}</div>
          <div><span className="font-medium">QR links to:</span> <code className="text-pf-accent">{buildSpoolUrl(spool.id)}</code></div>
        </div>
      </div>
    </Modal>
  );
}

