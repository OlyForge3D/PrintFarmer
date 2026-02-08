import React, { useEffect, useState } from 'react';
import { useParams } from 'react-router';
import { Button, Input, FormField, Alert, Card } from '@/common/components/ui';
import { apiClient } from '@/services/api';

interface JobStatus {
  id: string;
  status: 'pending' | 'processing' | 'completed' | 'failed';
  createdAt: string;
  completedAt?: string;
  scheduledAt?: string;
  progress?: number;
  retryCount?: number;
  workerId?: string;
  errorMessage?: string;
  result?: {
    gcodeUrl: string;
    printTime: number;
    filamentUsed: number;
  };
}

export function SlicerJobStatus({ initialId }: { initialId?: string }) {
  const params = useParams<{ id?: string }>();
  const routeId = initialId ?? params.id;

  const [jobId, setJobId] = useState(routeId ?? '');
  const [status, setStatus] = useState<JobStatus | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const fetchStatus = async (idToFetch?: string) => {
    const id = idToFetch ?? jobId;
    if (!id) return;

    setError(null);
    setLoading(true);
    setStatus(null);
    try {
      const result = await apiClient.getSlicerJobStatus(id);
      setStatus((result as unknown as JobStatus));
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : String(err));
    } finally { setLoading(false); }
  };

  useEffect(() => {
    // if route has id, ensure input is populated and fetch automatically
    if (routeId) {
      setJobId(routeId);
      fetchStatus(routeId);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [routeId]);

  return (
    <div>
      <Card>
        <Card.Body>
          <div className="space-y-4">
            <FormField label="Job ID">
              <div className="flex gap-2">
                <Input
                  value={jobId}
                  onChange={(e: React.ChangeEvent<HTMLInputElement>) => setJobId(e.target.value)}
                  placeholder="Enter job GUID"
                  onKeyPress={(e) => e.key === 'Enter' && fetchStatus()}
                />
                <Button
                  variant="primary"
                  onClick={() => fetchStatus()}
                  disabled={loading || !jobId}
                >
                  Fetch
                </Button>
              </div>
            </FormField>

            {loading && <div className="text-sm text-pf-text-secondary">Loading...</div>}
            {error && <Alert type="error" title="Error">{error}</Alert>}

            {status && (
              <Card className="mt-4">
                <Card.Body className="space-y-3">
                  <div>
                    <strong className="text-pf-text-primary">Status:</strong>
                    <span className="ml-2 text-pf-text-secondary">{status.status}</span>
                  </div>
                  <div>
                    <strong className="text-pf-text-primary">Progress:</strong>
                    <span className="ml-2 text-pf-text-secondary">{status.progress}%</span>
                  </div>
                  <div>
                    <strong className="text-pf-text-primary">Retry Count:</strong>
                    <span className="ml-2 text-pf-text-secondary">{status.retryCount}</span>
                  </div>
                  <div>
                    <strong className="text-pf-text-primary">Scheduled At:</strong>
                    <span className="ml-2 text-pf-text-secondary">
                      {status.scheduledAt ? new Date(status.scheduledAt).toLocaleString() : 'Not scheduled'}
                    </span>
                  </div>
                  <div>
                    <strong className="text-pf-text-primary">Worker:</strong>
                    <span className="ml-2 text-pf-text-secondary">{status.workerId ?? 'N/A'}</span>
                  </div>
                  <div>
                    <strong className="text-pf-text-primary">Message:</strong>
                    <span className="ml-2 text-pf-text-secondary">{status.errorMessage ?? 'None'}</span>
                  </div>
                </Card.Body>
              </Card>
            )}
          </div>
        </Card.Body>
      </Card>
    </div>
  );
}

export default SlicerJobStatus;
