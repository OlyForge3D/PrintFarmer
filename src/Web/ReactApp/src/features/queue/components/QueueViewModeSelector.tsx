import { ViewToggle } from '@/common/components/ui';

export type QueueViewMode = 'table' | 'list' | 'cards';

interface QueueViewModeSelectorProps {
  value: QueueViewMode;
  onChange: (mode: QueueViewMode) => void;
}

export function QueueViewModeSelector({ value, onChange }: QueueViewModeSelectorProps) {
  return <ViewToggle value={value} onChange={onChange} size="md" ariaLabel="Queue view mode" options={[
    { mode: 'table', kind: 'table', title: 'Table view' },
    { mode: 'list', kind: 'list', title: 'List view' },
    { mode: 'cards', kind: 'cards', title: 'Cards view' },
  ]} />;
}
