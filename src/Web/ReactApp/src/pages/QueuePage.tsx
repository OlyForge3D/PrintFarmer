import React from 'react';
import { QueueOverview } from '@/components/queue/QueueOverview';
import { PageTemplate } from '@/components/PageTemplate';
import { ListIcon } from '@/components/icons/MdiIcons';

export const QueuePage: React.FC = () => {
  return (
    <PageTemplate
      title="Print Queue"
      subtitle="Manage and monitor your print queue"
      icon={ListIcon}
      maxWidth="max-w-7xl"
    >
      <QueueOverview />
    </PageTemplate>
  );
};