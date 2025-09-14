import React, { useState } from 'react';

export const SlicerJobStatusPage: React.FC = () => {
    const [jobId, setJobId] = useState('');
    const [status, setStatus] = useState<any | null>(null);
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
                <h1 className="text-2xl font-bold">Slicer Job Status</h1>
                <p className="text-gray-500">Query a job to view scheduling and retry metadata.</p>
            </div>

            <div className="bg-white p-4 rounded shadow">
                <label className="block font-medium mb-2">Job ID</label>
                <div className="flex gap-2">
                    <input value={jobId} onChange={(e: React.ChangeEvent<HTMLInputElement>) => setJobId(e.target.value)} placeholder="Enter job GUID" className="border rounded px-2 py-1 flex-1" />
                    <button onClick={fetchStatus} disabled={loading || !jobId} className="px-3 py-1 bg-blue-600 text-white rounded">Fetch</button>
                </div>

                {loading && <div className="mt-3 text-sm text-gray-500">Loading...</div>}
                {error && <div className="mt-3 text-sm text-red-600">{error}</div>}

                {status && (
                    <div className="mt-4 bg-gray-50 p-3 rounded">
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