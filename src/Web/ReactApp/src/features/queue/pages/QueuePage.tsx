import React from 'react';
import { QueueOverview } from '@/features/queue/components/QueueOverview';
import { PageTemplate } from '@/common/components/PageTemplate';
import { ListIcon } from '@/common/components/icons/MdiIcons';

export const QueuePage: React.FC = () => {
  return (
    <PageTemplate
      title="Print Queue"
      subtitle="Manage and monitor your print queue"
      icon={ListIcon}
    >
      <QueueOverview />
    </PageTemplate>
  );
};