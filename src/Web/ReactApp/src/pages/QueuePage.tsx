import React from 'react';
import { QueueOverview } from '@/components/queue/QueueOverview';
import { PageTemplate } from '@/components/PageTemplate';
import { ListOrdered } from 'lucide-react';

export const QueuePage: React.FC = () => {
  return (
    <PageTemplate
      title="Print Queue"
      subtitle="Manage and monitor your print queue"
      icon={ListOrdered}
      maxWidth="max-w-7xl"
    >
      <QueueOverview />
    </PageTemplate>
  );
};