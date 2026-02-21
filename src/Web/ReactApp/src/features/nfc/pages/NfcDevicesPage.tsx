import { useState } from 'react';
import { Button, Card, Badge, Spinner } from '@/common/components/ui';
import { PageTemplate } from '@/common/components/PageTemplate';
import { useNfcDevices, useDeleteNfcDevice } from '@/common/hooks/useApi';
import { NfcDeviceDetailModal } from '@/features/nfc/components/NfcDeviceDetailModal';
import type { NfcDeviceDto } from '@/types/api';

function formatTimeAgo(dateStr?: string): string {
  if (!dateStr) return 'Never';
  const diff = Date.now() - new Date(dateStr).getTime();
  const mins = Math.floor(diff / 60000);
  if (mins < 1) return 'Just now';
  if (mins < 60) return `${mins}m ago`;
  const hours = Math.floor(mins / 60);
  if (hours < 24) return `${hours}h ago`;
  return `${Math.floor(hours / 24)}d ago`;
}

function SignalBadge({ rssi }: { rssi?: number }) {
  if (rssi === undefined || rssi === null) return <Badge variant="default">N/A</Badge>;
  if (rssi > -50) return <Badge variant="success">Strong ({rssi})</Badge>;
  if (rssi > -70) return <Badge variant="primary">Good ({rssi})</Badge>;
  if (rssi > -80) return <Badge variant="warning">Weak ({rssi})</Badge>;
  return <Badge variant="error">Poor ({rssi})</Badge>;
}

export function NfcDevicesPage() {
  const { data: devices = [], isLoading, error } = useNfcDevices();
  const deleteMutation = useDeleteNfcDevice();
  const [selectedDevice, setSelectedDevice] = useState<NfcDeviceDto | null>(null);

  if (isLoading) {
    return <PageTemplate title="NFC Devices"><Spinner size="lg" /></PageTemplate>;
  }

  if (error) {
    return (
      <PageTemplate title="NFC Devices">
        <div className="p-4 text-pf-error">Failed to load NFC devices: {String(error)}</div>
      </PageTemplate>
    );
  }

  return (
    <PageTemplate
      title="NFC Devices"
      subtitle={`${devices.length} registered reader${devices.length !== 1 ? 's' : ''}`}
    >
      {devices.length === 0 ? (
        <Card>
          <Card.Body>
            <div className="text-center py-8 text-pf-text-secondary">
              <p className="text-lg mb-2">No NFC devices registered</p>
              <p className="text-sm">
                NFC reader devices will appear here automatically when they send their first heartbeat.
                Configure your ESP32 FilaMan device with this PrintFarmer server URL and a printer ID.
              </p>
            </div>
          </Card.Body>
        </Card>
      ) : (
        <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-3">
          {devices.map((device) => (
            <Card key={device.id}>
              <Card.Header>
                <div className="flex items-center justify-between">
                  <span className="font-medium text-pf-text-primary">{device.name}</span>
                  <Badge variant={device.isOnline ? 'success' : 'error'}>
                    {device.isOnline ? 'Online' : 'Offline'}
                  </Badge>
                </div>
              </Card.Header>
              <Card.Body>
                <div className="space-y-2 text-sm">
                  {device.printerName && (
                    <div className="flex justify-between">
                      <span className="text-pf-text-secondary">Printer</span>
                      <span className="text-pf-text-primary">{device.printerName}</span>
                    </div>
                  )}
                  <div className="flex justify-between">
                    <span className="text-pf-text-secondary">IP</span>
                    <span className="text-pf-text-primary">{device.ipAddress || 'Unknown'}</span>
                  </div>
                  <div className="flex justify-between">
                    <span className="text-pf-text-secondary">WiFi</span>
                    <SignalBadge rssi={device.wifiRssi} />
                  </div>
                  <div className="flex justify-between">
                    <span className="text-pf-text-secondary">NFC Reader</span>
                    <Badge variant={device.nfcReaderOk ? 'success' : 'error'}>
                      {device.nfcReaderOk ? 'OK' : 'Error'}
                    </Badge>
                  </div>
                  {device.firmwareVersion && (
                    <div className="flex justify-between">
                      <span className="text-pf-text-secondary">Firmware</span>
                      <span className="text-pf-text-primary">v{device.firmwareVersion}</span>
                    </div>
                  )}
                  <div className="flex justify-between">
                    <span className="text-pf-text-secondary">Last Heartbeat</span>
                    <span className="text-pf-text-primary">{formatTimeAgo(device.lastHeartbeat)}</span>
                  </div>
                  <div className="flex justify-between">
                    <span className="text-pf-text-secondary">Last Scan</span>
                    <span className="text-pf-text-primary">{formatTimeAgo(device.lastScanAt)}</span>
                  </div>
                  {device.lastScannedSpoolId != null && (
                    <div className="flex justify-between">
                      <span className="text-pf-text-secondary">Last Spool</span>
                      <span className="text-pf-text-primary">#{device.lastScannedSpoolId}</span>
                    </div>
                  )}
                </div>
              </Card.Body>
              <Card.Footer>
                <div className="flex gap-2">
                  <Button
                    variant="secondary"
                    size="sm"
                    onClick={() => setSelectedDevice(device)}
                  >
                    Details
                  </Button>
                  <Button
                    variant="danger"
                    size="sm"
                    loading={deleteMutation.isPending}
                    onClick={() => {
                      if (confirm(`Remove NFC device "${device.name}"?`)) {
                        deleteMutation.mutate(device.id);
                      }
                    }}
                  >
                    Remove
                  </Button>
                </div>
              </Card.Footer>
            </Card>
          ))}
        </div>
      )}

      {selectedDevice && (
        <NfcDeviceDetailModal
          device={selectedDevice}
          isOpen={!!selectedDevice}
          onClose={() => setSelectedDevice(null)}
        />
      )}
    </PageTemplate>
  );
}
