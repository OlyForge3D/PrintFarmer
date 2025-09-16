import React, { useState } from 'react';

interface DryRunResult {
  rendered: string;
  issues?: string[];
  warnings?: string[];
  samplePlaceholders?: Record<string, unknown>;
}

export const SlicerDryRunPage: React.FC = () => {
    const [template, setTemplate] = useState<string>('');
    const [engine, setEngine] = useState<string>('OrcaSlicer');
    const [result, setResult] = useState<DryRunResult | null>(null);
    const [loading, setLoading] = useState(false);
    const [error, setError] = useState<string | null>(null);

    const doDryRun = async () => {
        setError(null);
        setLoading(true);
        setResult(null);
        try {
            const res = await fetch('/api/admin/slicer/dryrun', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ template, engine })
            });
            if (!res.ok) {
                const text = await res.text();
                throw new Error(text || 'Dry run failed');
            }
            const json = await res.json();
            setResult(json);
        } catch (err: unknown) {
            setError(err instanceof Error ? err.message : String(err));
        } finally { setLoading(false); }
    };

    return (
        <div className="space-y-6">
            <div>
                <h1 className="text-2xl font-bold text-pf-text-primary">Slicer Args Dry Run</h1>
                <p className="text-pf-text-secondary">Validate argument templates without executing the slicer binary.</p>
            </div>

            <div className="bg-pf-bg-1 p-4 rounded shadow-lg border border-pf-border">
                <label className="block mb-2 font-medium text-pf-text-primary">Engine</label>
                <select value={engine} onChange={(e: React.ChangeEvent<HTMLSelectElement>) => setEngine(e.target.value)} className="border border-pf-border rounded px-2 py-1 bg-pf-bg-0 text-pf-text-primary">
                    <option>OrcaSlicer</option>
                    <option>PrusaSlicer</option>
                </select>

                <label className="block mt-4 mb-2 font-medium text-pf-text-primary">Args Template</label>
                <textarea value={template} onChange={(e: React.ChangeEvent<HTMLTextAreaElement>) => setTemplate(e.target.value)} rows={6} className="w-full border border-pf-border rounded p-2 bg-pf-bg-0 text-pf-text-primary focus:outline-none focus:ring-2 focus:ring-pf-accent" placeholder={'e.g. --config "{config}" --output "{output}" {input}'} />

                <div className="mt-4 flex gap-2">
                    <button onClick={doDryRun} disabled={loading} className="px-4 py-2 bg-pf-accent text-white rounded hover:bg-pf-success-hover disabled:opacity-50">Validate Template</button>
                    <button onClick={() => { setTemplate('--export-gcode -o {output} {input}'); }} className="px-4 py-2 bg-pf-bg-2 text-pf-text-primary rounded hover:bg-pf-bg-1 border border-pf-border">Insert Example</button>
                </div>

                {loading && <div className="mt-3 text-sm text-pf-text-secondary">Validating...</div>}
                {error && <div className="mt-3 text-sm text-pf-error-text">{error}</div>}

                {result && (
                    <div className="mt-4 bg-pf-bg-2 p-3 rounded border border-pf-border">
                        <div className="font-medium text-pf-text-primary">Rendered Args</div>
                        <pre className="text-sm mt-2 bg-pf-bg-0 p-2 border border-pf-border rounded text-pf-text-primary">{result.rendered}</pre>

                        <div className="mt-3">
                            <div className="font-medium text-pf-text-primary">Issues</div>
                            <ul className="list-disc ml-6 mt-2 text-sm text-pf-error-text">
                                {result.issues && result.issues.length > 0 ? result.issues.map((i: string, idx: number) => <li key={idx}>{i}</li>) : <li className="text-pf-text-secondary">No issues detected</li>}
                            </ul>
                        </div>
                        <div className="mt-3">
                            <div className="font-medium text-pf-text-primary">Warnings</div>
                            <ul className="list-disc ml-6 mt-2 text-sm text-pf-warning-text">
                                {result.warnings && result.warnings.length > 0 ? result.warnings.map((w: string, idx: number) => <li key={idx}>{w}</li>) : <li className="text-pf-text-secondary">No warnings</li>}
                            </ul>
                        </div>

                        <div className="mt-3">
                            <div className="font-medium text-pf-text-primary">Sample placeholders</div>
                            <pre className="text-sm mt-2 bg-pf-bg-0 p-2 border border-pf-border rounded text-pf-text-primary">{JSON.stringify(result.samplePlaceholders, null, 2)}</pre>
                        </div>
                    </div>
                )}
            </div>
        </div>
    );
};

export default SlicerDryRunPage;