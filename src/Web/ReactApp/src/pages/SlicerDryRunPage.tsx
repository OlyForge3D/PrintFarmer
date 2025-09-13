import React, { useState } from 'react';

export const SlicerDryRunPage: React.FC = () => {
    const [template, setTemplate] = useState<string>('');
    const [engine, setEngine] = useState<string>('OrcaSlicer');
    const [result, setResult] = useState<any | null>(null);
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
                <h1 className="text-2xl font-bold">Slicer Args Dry Run</h1>
                <p className="text-gray-500">Validate argument templates without executing the slicer binary.</p>
            </div>

            <div className="bg-white p-4 rounded shadow">
                <label className="block mb-2 font-medium">Engine</label>
                <select value={engine} onChange={(e: React.ChangeEvent<HTMLSelectElement>) => setEngine(e.target.value)} className="border rounded px-2 py-1">
                    <option>OrcaSlicer</option>
                    <option>PrusaSlicer</option>
                </select>

                <label className="block mt-4 mb-2 font-medium">Args Template</label>
                <textarea value={template} onChange={(e: React.ChangeEvent<HTMLTextAreaElement>) => setTemplate(e.target.value)} rows={6} className="w-full border rounded p-2" placeholder={'e.g. --config "{config}" --output "{output}" {input}'} />

                <div className="mt-4 flex gap-2">
                    <button onClick={doDryRun} disabled={loading} className="px-4 py-2 bg-blue-600 text-white rounded">Validate Template</button>
                    <button onClick={() => { setTemplate('--export-gcode -o {output} {input}'); }} className="px-4 py-2 bg-gray-200 rounded">Insert Example</button>
                </div>

                {loading && <div className="mt-3 text-sm text-gray-500">Validating...</div>}
                {error && <div className="mt-3 text-sm text-red-600">{error}</div>}

                {result && (
                    <div className="mt-4 bg-gray-50 p-3 rounded">
                        <div className="font-medium">Rendered Args</div>
                        <pre className="text-sm mt-2 bg-white p-2 border rounded">{result.rendered}</pre>

                        <div className="mt-3">
                            <div className="font-medium">Issues</div>
                            <ul className="list-disc ml-6 mt-2 text-sm text-red-600">
                                {result.issues && result.issues.length > 0 ? result.issues.map((i: string, idx: number) => <li key={idx}>{i}</li>) : <li className="text-gray-600">No issues detected</li>}
                            </ul>
                        </div>
                        <div className="mt-3">
                            <div className="font-medium">Warnings</div>
                            <ul className="list-disc ml-6 mt-2 text-sm text-yellow-700">
                                {result.warnings && result.warnings.length > 0 ? result.warnings.map((w: string, idx: number) => <li key={idx}>{w}</li>) : <li className="text-gray-600">No warnings</li>}
                            </ul>
                        </div>

                        <div className="mt-3">
                            <div className="font-medium">Sample placeholders</div>
                            <pre className="text-sm mt-2 bg-white p-2 border rounded">{JSON.stringify(result.samplePlaceholders, null, 2)}</pre>
                        </div>
                    </div>
                )}
            </div>
        </div>
    );
};

export default SlicerDryRunPage;