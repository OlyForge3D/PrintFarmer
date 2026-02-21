import { useState } from 'react';
import { Card } from '@/common/components/ui';

const DASHBOARD_UID = 'printfarmer-overview';

const PANELS = [
  { id: 5, title: 'Request Rate Over Time', span: 6 },
  { id: 6, title: 'Response Latency Distribution', span: 6 },
  { id: 7, title: 'Printer Operations', span: 6 },
  { id: 8, title: 'Slicer Operations', span: 6 },
];

export function GrafanaEmbedPanels() {
  return (
    <div>
      <h3 className="text-sm font-medium text-pf-text-secondary mb-3">Live Charts</h3>
      <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
        {PANELS.map(panel => (
          <GrafanaPanel key={panel.id} panelId={panel.id} title={panel.title} />
        ))}
      </div>
    </div>
  );
}

function GrafanaPanel({ panelId, title }: { panelId: number; title: string }) {
  const [hasError, setHasError] = useState(false);
  const src = `/grafana/d-solo/${DASHBOARD_UID}/printfarmer-overview?panelId=${panelId}&refresh=30s&theme=dark`;

  if (hasError) {
    return (
      <Card>
        <Card.Body className="h-[250px] flex items-center justify-center text-pf-text-secondary text-sm">
          Unable to load "{title}" panel
        </Card.Body>
      </Card>
    );
  }

  return (
    <Card>
      <Card.Body className="p-0 overflow-hidden rounded-lg">
        <iframe
          src={src}
          title={title}
          className="w-full h-[250px] border-0"
          onError={() => setHasError(true)}
          loading="lazy"
        />
      </Card.Body>
    </Card>
  );
}
