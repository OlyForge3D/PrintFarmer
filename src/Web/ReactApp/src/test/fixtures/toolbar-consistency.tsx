import { useState } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { TableFiltersBar } from '@/features/queue/components/QueueFiltersBar';
import HistoryFiltersBar from '@/features/queue/components/HistoryFiltersBar';
import QueueDateRangeBar, { type DateRange } from '@/features/queue/components/QueueDateRangeBar';
import QueueTimelineTab from '@/features/queue/components/QueueTimelineTab';
import { QueueViewModeSelector, type QueueViewMode } from '@/features/queue/components/QueueViewModeSelector';
import { Button, HeaderActions, SearchField, SortControl, Toolbar, ToolbarField, ViewToggle, RefreshButton, TimePeriodFilter } from '@/common/components/ui';
import type { TimePeriodFilterValue } from '@/common/components/ui';
import type { QueueOverviewDto, TimelineEventDto } from '@/types/api';

function Fixture() {
  const [statuses, setStatuses] = useState(['completed', 'failed', 'cancelled']);
  const [sort, setSort] = useState<'newest' | 'oldest' | 'duration' | 'model'>('newest');
  const [historyView, setHistoryView] = useState<'cards' | 'table'>('cards');
  const [view, setView] = useState<QueueViewMode>('table');
  const [search, setSearch] = useState('');
  const [direction, setDirection] = useState<'asc' | 'desc'>('asc');
  const [date, setDate] = useState<DateRange>({ from: null, to: null });
  const [period, setPeriod] = useState<TimePeriodFilterValue>({ type: 'preset', days: 30 });
  return <main className="p-4 min-w-0 bg-pf-bg-2 text-pf-text-primary">
    <h1 className="text-2xl">Toolbar consistency</h1>
    <HeaderActions><Button variant="primary">Create specimen</Button></HeaderActions>
    <Toolbar labelled aria-label="Specimen controls">
      <SearchField value={search} onChange={event => setSearch(event.target.value)} onClear={() => setSearch('')} label="Search specimens" placeholder="Search specimens..." />
      <ToolbarField label="Sort by" htmlFor="specimen-sort">
        <SortControl id="specimen-sort" direction={direction} onDirectionChange={() => setDirection(direction === 'asc' ? 'desc' : 'asc')}>
          <option>Name</option><option>Last modified</option>
        </SortControl>
      </ToolbarField>
      <Toolbar.Spacer />
      <ViewToggle value="grid" onChange={() => {}} size="md" options={[{ mode: 'grid', icon: 'grid', title: 'Grid view' }, { mode: 'table', icon: 'table', title: 'Table view' }]} />
      <RefreshButton />
    </Toolbar>
    <h2>Queue</h2>
    <TableFiltersBar onStatusChange={() => {}} onModelChange={() => {}} onMaterialChange={() => {}}
      onSortChange={() => {}} onRefresh={() => {}} viewControls={<QueueViewModeSelector value={view} onChange={setView} />} />
    <h2>History</h2>
    <HistoryFiltersBar selectedStatuses={statuses} onStatusChange={setStatuses} sortBy={sort}
      onSortChange={setSort} onRefresh={async () => {}} isLoading={false}
      viewMode={historyView} onViewModeChange={setHistoryView} />
    <QueueDateRangeBar dateFrom={date.from} dateTo={date.to} onChange={setDate} />
    <TimePeriodFilter value={period} onChange={setPeriod} />
    <QueueTimelineTab stats={null} dateFrom={null} dateTo={null} />
    <section aria-label="Excluded control baseline"><Button size="sm">Unmigrated button</Button></section>
  </main>;
}

export function ToolbarConsistencyFixture() {
  const [client] = useState(() => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const timeline: TimelineEventDto[] = [{
      jobId: 'fixture-job', jobName: 'Fixture gear', printerName: 'Fixture printer', state: 'Printing',
      enteredAtUtc: new Date(Date.now() - 3_600_000).toISOString(),
    }];
    const overview: QueueOverviewDto[] = [{
      printerId: 'fixture-printer', printerName: 'Fixture printer', printerModel: 'Fixture model',
      isAvailable: false, queuedJobsCount: 1, currentJobId: 'fixture-job',
    }];
    client.setQueryDefaults(['gantt-timeline'], { initialData: timeline, enabled: false });
    client.setQueryDefaults(['gantt-overview'], { initialData: overview, enabled: false });
    return client;
  });
  return <QueryClientProvider client={client}><Fixture /></QueryClientProvider>;
}
