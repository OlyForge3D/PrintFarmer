import React, { useEffect, useState } from 'react';
import { useParams } from 'react-router-dom';
import { queueService, PrintJob } from '@/services/queueService';
import { Button } from '@/common/components/ui';
import PrinterQueueHistory from '@/features/queue/components/PrinterQueueHistory';

export const PrinterQueuePage: React.FC = () => {
  const { id } = useParams<{ id: string }>();
  const [jobs, setJobs] = useState<PrintJob[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!id) return;
    (async () => {
      try {
        setLoading(true);
        const q = await queueService.getPrinterQueue(id);
        setJobs(q || []);
        setLoading(false);
      } catch (err) {
        setError(err instanceof Error ? err.message : 'Failed to load printer queue');
        setLoading(false);
      }
    })();
  }, [id]);

  if (!id) return <div>Printer ID missing</div>;

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <h2 className="text-2xl font-bold">Printer Queue</h2>
        <div className="flex gap-2">
          <Button variant="secondary" onClick={() => window.history.back()}>Back</Button>
        </div>
      </div>

      {loading && <div>Loading…</div>}
      {error && <div className="text-red-600">{error}</div>}

      {!loading && (
        <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
          <div className="bg-white p-4 rounded border">
            <h3 className="font-semibold mb-2">Queued Jobs</h3>
            {jobs.length === 0 && <div className="text-sm text-gray-600">No queued jobs</div>}
            <ul className="space-y-2">
              {jobs.map(job => (
                <li key={job.id} className="p-2 border rounded flex justify-between items-center">
                  <div>
                    <div className="font-medium">{job.gcodeFileName}</div>
                    <div className="text-sm text-gray-500">Status: {job.status}</div>
                  </div>
                  <div className="text-sm text-gray-600">Pos {job.queuePosition}</div>
                </li>
              ))}
            </ul>
          </div>

          <div>
            <PrinterQueueHistory printerId={id} />
          </div>
        </div>
      )}
    </div>
  );
};
