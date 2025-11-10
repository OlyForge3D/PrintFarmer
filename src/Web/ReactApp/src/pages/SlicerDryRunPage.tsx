import React, { useState, useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { renderUnknown } from '@/utils/renderUnknown';
import { PageTemplate } from '@/components/PageTemplate';
import { TestTube } from 'lucide-react';
import { getApiBaseUrl, getAuthHeaders } from '@/utils/apiUrlHelpers';
import { slicerRegistry } from '@/services/slicerRegistry';

interface DryRunResult {
    rendered: string;
    issues?: string[];
    warnings?: string[];
    samplePlaceholders?: Record<string, unknown>;
}

export const SlicerDryRunPage: React.FC = () => {
    const [template, setTemplate] = useState<string>('');
    const [engine, setEngine] = useState<string>('');
    const [result, setResult] = useState<DryRunResult | null>(null);
    const [loading, setLoading] = useState(false);
    const [error, setError] = useState<string | null>(null);

    // Fetch available slicers
    const { data: availableSlicers = [] } = useQuery({
        queryKey: ['slicers-available'],
        queryFn: () => slicerRegistry.getSlicers(),
        staleTime: 10_000,
        refetchInterval: 15_000,
    });

    // Extract slicer types
    const slicerTypes = useMemo(() => {
        return availableSlicers
            .map(s => s.slicerType || s.name || '')
            .filter((v, i, arr) => v && arr.indexOf(v) === i)
            .sort();
    }, [availableSlicers]);

    // Set initial engine
    React.useEffect(() => {
        if (!engine && slicerTypes.length > 0) {
            setEngine(slicerTypes[0]);
        }
    }, [slicerTypes, engine]);

    const doDryRun = async () => {
        setError(null);
        setLoading(true);
        setResult(null);
        try {
            const res = await fetch(`${getApiBaseUrl()}/admin/slicer/dryrun`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json', ...getAuthHeaders() },
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
        <PageTemplate
            title="Slicer Args Dry Run"
            subtitle="Validate argument templates without executing the slicer binary"
            icon={TestTube}
            maxWidth="max-w-4xl"
        >

            <div className="card">
                <div className="form-group">
                    <label className="form-label">Engine</label>
                    <select aria-label="Slicer engine" value={engine} onChange={(e: React.ChangeEvent<HTMLSelectElement>) => setEngine(e.target.value)} className="input-base">
                        {slicerTypes.map(s => <option key={s}>{s}</option>)}
                    </select>
                </div>

                <div className="form-group">
                    <label className="form-label">Args Template</label>
                    <textarea value={template} onChange={(e: React.ChangeEvent<HTMLTextAreaElement>) => setTemplate(e.target.value)} rows={6} className="input-base w-full" placeholder={'e.g. --config "{config}" --output "{output}" {input}'} />
                </div>

                <div className="gap-md flex-row">
                    <button onClick={doDryRun} disabled={loading} className="btn-base btn-md btn-primary">Validate Template</button>
                    <button onClick={() => { setTemplate('--export-gcode -o {output} {input}'); }} className="btn-base btn-md btn-secondary">Insert Example</button>
                </div>

                {loading && <div className="mt-3 text-sm text-pf-text-secondary">Validating...</div>}
                {error && <div className="alert-base alert-error mt-3">{error}</div>}

                {result && (
                    <div className="card mt-4">
                        <div className="font-medium text-pf-text-primary">Rendered Args</div>
                        <pre className="text-sm mt-2 bg-pf-bg-0 p-2 border border-pf-border rounded text-pf-text-primary">{renderUnknown(result.rendered)}</pre>

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
                            <div className="text-sm mt-2 bg-pf-bg-0 p-2 border border-pf-border rounded text-pf-text-primary">
                                {renderUnknown(result.samplePlaceholders)}
                            </div>
                        </div>
                    </div>
                )}
            </div>
        </PageTemplate>
    );
};

export default SlicerDryRunPage;