import { Modal } from '@/common/components/modals/Modal';
import { Badge, Spinner } from '@/common/components/ui';
import { useNfcDeviceScanHistory } from '@/common/hooks/useApi';
import type { NfcDeviceDto, NfcScanHistoryDto } from '@/types/api';

interface NfcDeviceDetailModalProps {
  device: NfcDeviceDto;
  isOpen: boolean;
  onClose: () => void;
}

function formatDate(dateStr?: string): string {
  if (!dateStr) return 'N/A';
  return new Date(dateStr).toLocaleString();
}

export function NfcDeviceDetailModal({ device, isOpen, onClose }: NfcDeviceDetailModalProps) {
  const { data: history = [], isLoading: historyLoading } = useNfcDeviceScanHistory(device.id);

  return (
    <Modal isOpen={isOpen} onClose={onClose} title={device.name} size="lg">
      <div className="space-y-6">
        <div className="grid grid-cols-2 gap-4 text-sm">
          <div>
            <span className="text-pf-text-secondary block">Status</span>
            <Badge variant={device.isOnline ? 'success' : 'error'}>
              {device.isOnline ? 'Online' : 'Offline'}
            </Badge>
          </div>
          <div>
            <span className="text-pf-text-secondary block">Printer</span>
            <span className="text-pf-text-primary">{device.printerName || 'Unassigned'}</span>
          </div>
          <div>
            <span className="text-pf-text-secondary block">IP Address</span>
            <span className="text-pf-text-primary">{device.ipAddress || 'Unknown'}</span>
          </div>
          <div>
            <span className="text-pf-text-secondary block">Firmware</span>
            <span className="text-pf-text-primary">{device.firmwareVersion ? `v${device.firmwareVersion}` : 'Unknown'}</span>
          </div>
          <div>
            <span className="text-pf-text-secondary block">WiFi RSSI</span>
            <span className="text-pf-text-primary">{device.wifiRssi ?? 'N/A'} dBm</span>
          </div>
          <div>
            <span className="text-pf-text-secondary block">Free Heap</span>
            <span className="text-pf-text-primary">{device.freeHeap ? `${(device.freeHeap / 1024).toFixed(1)} KB` : 'N/A'}</span>
          </div>
          <div>
            <span className="text-pf-text-secondary block">NFC Reader</span>
            <Badge variant={device.nfcReaderOk ? 'success' : 'error'}>
              {device.nfcReaderOk ? 'OK' : 'Error'}
            </Badge>
          </div>
          <div>
            <span className="text-pf-text-secondary block">Registered</span>
            <span className="text-pf-text-primary">{formatDate(device.createdAt)}</span>
          </div>
          <div>
            <span className="text-pf-text-secondary block">Last Heartbeat</span>
            <span className="text-pf-text-primary">{formatDate(device.lastHeartbeat)}</span>
          </div>
          <div>
            <span className="text-pf-text-secondary block">Last Scan</span>
            <span className="text-pf-text-primary">{formatDate(device.lastScanAt)}</span>
          </div>
        </div>

        <div>
          <h3 className="text-lg font-medium text-pf-text-primary mb-3">Scan History</h3>
          {historyLoading ? (
            <Spinner size="md" />
          ) : history.length === 0 ? (
            <p className="text-pf-text-secondary text-sm">No scan events recorded yet.</p>
          ) : (
            <div className="overflow-x-auto">
              <table className="w-full text-sm">
                <thead>
                  <tr className="border-b border-pf-border">
                    <th className="text-left py-2 text-pf-text-secondary font-medium">Time</th>
                    <th className="text-left py-2 text-pf-text-secondary font-medium">Spool</th>
                    <th className="text-left py-2 text-pf-text-secondary font-medium">Format</th>
                    <th className="text-left py-2 text-pf-text-secondary font-medium">Material</th>
                    <th className="text-left py-2 text-pf-text-secondary font-medium">Action</th>
                  </tr>
                </thead>
                <tbody>
                  {history.map((event: NfcScanHistoryDto) => (
                    <tr key={event.id} className="border-b border-pf-border/50">
                      <td className="py-2 text-pf-text-primary">{formatDate(event.scannedAt)}</td>
                      <td className="py-2 text-pf-text-primary">{event.spoolId ? `#${event.spoolId}` : 'N/A'}</td>
                      <td className="py-2">
                        <Badge variant="default">{event.tagFormat}</Badge>
                      </td>
                      <td className="py-2 text-pf-text-primary">
                        {event.materialType || event.brandName
                          ? `${event.brandName || ''} ${event.materialType || ''}`.trim()
                          : 'N/A'}
                      </td>
                      <td className="py-2">
                        <Badge variant={event.action === 'spool_set' ? 'success' : 'default'}>
                          {event.action || 'unknown'}
                        </Badge>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      </div>
    </Modal>
  );
}
