import React, { useState } from 'react';
import { PageTemplate } from '@/components/PageTemplate';
import { ClipboardList } from 'lucide-react';
import { Button, Input, FormField, Alert, Card } from '@/components/ui';
import { getApiBaseUrl, getAuthHeaders } from '@/utils/apiUrlHelpers';

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

export const SlicerJobStatusPage: React.FC = () => {
    const [jobId, setJobId] = useState('');
    const [status, setStatus] = useState<JobStatus | null>(null);
    const [loading, setLoading] = useState(false);
    const [error, setError] = useState<string | null>(null);

    const fetchStatus = async () => {
        setError(null);
        setLoading(true);
        setStatus(null);
        try {
            const res = await fetch(`${getApiBaseUrl()}/slicer/jobs/${encodeURIComponent(jobId)}/status`, { headers: getAuthHeaders() });
            if (!res.ok) {
                if (res.status === 404) throw new Error('Job not found');
                throw new Error('Failed to fetch job status');
            }
            setStatus(await res.json());
        } catch (err: unknown) {
            setError(err instanceof Error ? err.message : String(err));
        } finally { setLoading(false); }
    };

    return (
        <PageTemplate
            title="Slicer Job Status"
            subtitle="Query a job to view scheduling and retry metadata"
            icon={ClipboardList}
            maxWidth="max-w-4xl"
        >

            <Card>
                <Card.Body>
                    <div className="space-y-4">
                        <FormField label="Job ID" error={jobId === '' ? undefined : undefined}>
                            <div className="flex gap-2">
                                <Input
                                    value={jobId}
                                    onChange={(e: React.ChangeEvent<HTMLInputElement>) => setJobId(e.target.value)}
                                    placeholder="Enter job GUID"
                                    onKeyPress={(e) => e.key === 'Enter' && fetchStatus()}
                                />
                                <Button
                                    variant="primary"
                                    onClick={fetchStatus}
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
        </PageTemplate>
    );
};

export default SlicerJobStatusPage;