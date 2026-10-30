import { ViewToggle } from '@/common/components/ui';

export type QueueViewMode = 'table' | 'list' | 'cards';

interface QueueViewModeSelectorProps {
  value: QueueViewMode;
  onChange: (mode: QueueViewMode) => void;
}

export function QueueViewModeSelector({ value, onChange }: QueueViewModeSelectorProps) {
  return <ViewToggle value={value} onChange={onChange} size="md" ariaLabel="Queue view mode" options={[
    { mode: 'table', icon: 'table', title: 'Table view', label: 'Table' },
    { mode: 'list', icon: 'list', title: 'List view', label: 'List' },
    { mode: 'cards', icon: 'grid', title: 'Cards view', label: 'Cards' },
  ]} />;
}
