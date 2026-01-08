import React from 'react';
import { usePrinters } from '@/features/printers/hooks/usePrinters';
import { Link } from 'react-router-dom';

export function QueueOverviewPage() {
  const { data: printers, isLoading } = usePrinters();

  return (
    <div className="p-4">
      <h1 className="text-2xl font-semibold mb-4">Print Queue Overview</h1>
      {isLoading && <div>Loading printers...</div>}
      {!isLoading && printers && (
        <div className="grid grid-cols-1 gap-3">
          {printers.map(p => (
            <Link key={p.id} to={`/queue/printer/${p.id}`} className="p-3 border rounded hover:bg-pf-bg-1">
              <div className="flex justify-between items-center">
                <div>
                  <div className="font-medium">{p.name}</div>
                  <div className="text-sm text-pf-muted">{p.model}</div>
                </div>
                <div className="text-sm text-pf-muted">{p.isOnline ? 'Online' : 'Offline'}</div>
              </div>
            </Link>
          ))}
        </div>
      )}
    </div>
  );
}

export default QueueOverviewPage;
