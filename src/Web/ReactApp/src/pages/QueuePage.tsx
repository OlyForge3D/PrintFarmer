import React from 'react';
import { QueueOverview } from '@/components/queue/QueueOverview';

export const QueuePage: React.FC = () => {
  return (
    <div className="container mx-auto px-4 py-8">
      <QueueOverview />
    </div>
  );
};