/* eslint-disable local/pf-no-unguarded-console */
import React, { useState, useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { renderUnknown } from '@/utils/renderUnknown';
import { Button, Select, Textarea, FormField } from '@/components/ui';
import { PageTemplate } from '@/components/PageTemplate';
import { TestIcon } from '@/components/icons/MdiIcons';
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
            icon={TestIcon}
            maxWidth="max-w-4xl"
        >

            <div className="card">
                <FormField label="Engine" inline={false}>
                    <Select aria-label="Slicer engine" value={engine} onChange={(e: React.ChangeEvent<HTMLSelectElement>) => setEngine(e.target.value)}>
                        {slicerTypes.map(s => <option key={s}>{s}</option>)}
                    </Select>
                </FormField>

                <FormField label="Args Template" inline={false}>
                    <Textarea value={template} onChange={(e: React.ChangeEvent<HTMLTextAreaElement>) => setTemplate(e.target.value)} rows={6} placeholder={'e.g. --config "{config}" --output "{output}" {input}'} />
                </FormField>

                <div className="gap-md flex-row">
                    <Button onClick={doDryRun} disabled={loading} variant="primary">Validate Template</Button>
                    <Button onClick={() => { setTemplate('--export-gcode -o {output} {input}'); }} variant="secondary">Insert Example</Button>
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