import React, { useState } from 'react';

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
            const res = await fetch(`/api/slicer/jobs/${encodeURIComponent(jobId)}/status`);
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
        <div className="space-y-6">
            <div>
                <h1 className="text-2xl font-bold text-pf-text-primary">Slicer Job Status</h1>
                <p className="text-pf-text-secondary">Query a job to view scheduling and retry metadata.</p>
            </div>

            <div className="bg-pf-bg-1 p-4 rounded border border-pf-border">
                <label className="block font-medium mb-2 text-pf-text-primary">Job ID</label>
                <div className="flex gap-2">
                    <input 
                        value={jobId} 
                        onChange={(e: React.ChangeEvent<HTMLInputElement>) => setJobId(e.target.value)} 
                        placeholder="Enter job GUID" 
                        className="border border-pf-border rounded px-2 py-1 flex-1 bg-pf-bg-0 text-pf-text-primary focus:outline-none focus:ring-2 focus:ring-pf-accent" 
                    />
                    <button 
                        onClick={fetchStatus} 
                        disabled={loading || !jobId} 
                        className="px-3 py-1 bg-pf-accent text-white rounded hover:bg-pf-accent-hover disabled:opacity-50 disabled:cursor-not-allowed"
                    >
                        Fetch
                    </button>
                </div>

                {loading && <div className="mt-3 text-sm text-pf-text-secondary">Loading...</div>}
                {error && <div className="mt-3 text-sm text-pf-error-text">{error}</div>}

                {status && (
                    <div className="mt-4 bg-pf-bg-2 p-3 rounded border border-pf-border text-pf-text-primary">
                        <div><strong>Status:</strong> {status.status}</div>
                        <div><strong>Progress:</strong> {status.progress}%</div>
                        <div><strong>Retry Count:</strong> {status.retryCount}</div>
                        <div><strong>Scheduled At:</strong> {status.scheduledAt ? new Date(status.scheduledAt).toLocaleString() : 'Not scheduled'}</div>
                        <div><strong>Worker:</strong> {status.workerId ?? 'N/A'}</div>
                        <div className="mt-3"><strong>Message:</strong> {status.errorMessage ?? 'None'}</div>
                    </div>
                )}
            </div>
        </div>
    );
};

export default SlicerJobStatusPage;