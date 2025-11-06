import React, { useState } from 'react';
import { PageTemplate } from '@/components/PageTemplate';
import { ClipboardList } from 'lucide-react';
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

            <div className="card">
                <div className="form-group">
                  <label className="form-label">Job ID</label>
                  <div className="gap-md flex-row">
                    <input 
                        value={jobId} 
                        onChange={(e: React.ChangeEvent<HTMLInputElement>) => setJobId(e.target.value)} 
                        placeholder="Enter job GUID" 
                        className="input-base flex-1" 
                    />
                    <button 
                        type="button"
                        onClick={fetchStatus} 
                        disabled={loading || !jobId} 
                        className="btn-base btn-md btn-primary"
                    >
                        Fetch
                    </button>
                  </div>
                </div>

                {loading && <div className="mt-3 text-sm text-pf-text-secondary">Loading...</div>}
                {error && <div className="alert-base alert-error mt-3">{error}</div>}

                {status && (
                    <div className="card mt-4">
                        <div><strong>Status:</strong> {status.status}</div>
                        <div><strong>Progress:</strong> {status.progress}%</div>
                        <div><strong>Retry Count:</strong> {status.retryCount}</div>
                        <div><strong>Scheduled At:</strong> {status.scheduledAt ? new Date(status.scheduledAt).toLocaleString() : 'Not scheduled'}</div>
                        <div><strong>Worker:</strong> {status.workerId ?? 'N/A'}</div>
                        <div className="mt-3"><strong>Message:</strong> {status.errorMessage ?? 'None'}</div>
                    </div>
                )}
            </div>
        </PageTemplate>
    );
};

export default SlicerJobStatusPage;