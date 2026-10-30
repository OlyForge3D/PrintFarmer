import { useMemo } from 'react';
import clsx from 'clsx';
import { PageTemplate } from '@/common/components/PageTemplate';
import type { EmbeddablePageProps } from '@/common/components/EmbeddablePageProps';
import {
  Button,
  Input,
  Select,
  Badge,
  Tooltip,
  Table,
  TableHead,
  TableBody,
  TableRow,
  TableHeaderCell,
  TableCell,
  Pagination,
  Toolbar,
  ToolbarField,
  FiltersDisclosure,
} from '@/common/components/ui';
import { AdminLoading, AdminError, AdminEmpty } from '@/common/components/admin';
import { ShieldIcon, RefreshIcon, CloseIcon } from '@/common/components/icons/MdiIcons';
import { useUrlFilterState } from '@/common/hooks/useUrlFilterState';
import { useLoginAudit } from '@/features/admin/hooks/useLoginAudit';

const UA_MAX_CHARS = 30;
const PAGE_SIZE_OPTIONS = [25, 50, 100, 200] as const;

const FILTER_CONFIG = {
  from: { key: 'from', type: 'string' as const, defaultValue: '' },
  to: { key: 'to', type: 'string' as const, defaultValue: '' },
  username: { key: 'username', type: 'string' as const, defaultValue: '', debounce: 400 },
  success: { key: 'success', type: 'string' as const, defaultValue: '' },
  page: { key: 'page', type: 'number' as const, defaultValue: 1, filterable: false },
  pageSize: { key: 'pageSize', type: 'number' as const, defaultValue: 50, filterable: false },
};

function formatLocalDateTime(iso: string): string {
  try {
    return new Date(iso).toLocaleString(undefined, {
      year: 'numeric',
      month: 'short',
      day: 'numeric',
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit',
    });
  } catch {
    return iso;
  }
}

function truncateUserAgent(ua: string): string {
  if (!ua) return '—';
  return ua.length > UA_MAX_CHARS ? `${ua.slice(0, UA_MAX_CHARS)}…` : ua;
}

export function LoginAuditPage({ embedded = false }: EmbeddablePageProps) {
  const {
    from, to, username, success, page, pageSize,
    setUsername,
    setMany, resetAll, hasActiveFilters,
  } = useUrlFilterState(FILTER_CONFIG);

  const auditFilters = useMemo(
    () => ({
      from: from || undefined,
      to: to || undefined,
      username: username || undefined,
      success: success === 'true' ? true : success === 'false' ? false : undefined,
      page: page as number,
      pageSize: pageSize as number,
    }),
    [from, to, username, success, page, pageSize],
  );

  const { data, isLoading, isError, error, isFetching, refetch } = useLoginAudit(auditFilters);

  const currentPage = (page as number);
  const currentPageSize = (pageSize as number);
  const totalCount = data?.totalCount ?? 0;
  const totalPages = totalCount > 0 ? Math.ceil(totalCount / currentPageSize) : 0;
  const rangeStart = totalCount === 0 ? 0 : (currentPage - 1) * currentPageSize + 1;
  const rangeEnd = Math.min(currentPage * currentPageSize, totalCount);

  const handleFilterWithPageReset = (updates: Record<string, string | number | boolean>) => {
    setMany({ ...updates, page: 1 } as Parameters<typeof setMany>[0]);
  };
  const refinementCount = [from, to, success].filter(Boolean).length;

  return (
    <PageTemplate
      title="Login Audit Log"
      subtitle="View all authentication attempts with timestamps, outcomes, and originating IP addresses"
      icon={ShieldIcon}
      embedded={embedded}
    >
      {/* Filter bar */}
      <Toolbar
        aria-label="Filter login audit entries"
        className="bg-pf-bg-1 border border-pf-border rounded-md p-4 mb-4"
        labelled
      >
          <ToolbarField label="Username" htmlFor="audit-username">
            <Input
              id="audit-username"
              className="pf-control py-0"
              data-role="field"
              type="text"
              value={username as string}
              onChange={(e) => setUsername(e.target.value)}
              placeholder="Partial match…"
              aria-label="Filter by username"
            />
          </ToolbarField>

        <FiltersDisclosure
          activeCount={refinementCount}
          onReset={() => handleFilterWithPageReset({ from: '', to: '', success: '' })}
        >
          <ToolbarField label="From" htmlFor="audit-from">
            <Input
              id="audit-from"
              className="pf-control py-0"
              data-role="field"
              type="datetime-local"
              value={from as string}
              onChange={(e) => handleFilterWithPageReset({ from: e.target.value })}
              aria-label="Filter from date and time"
            />
          </ToolbarField>

          <ToolbarField label="To" htmlFor="audit-to">
            <Input
              id="audit-to"
              className="pf-control py-0"
              data-role="field"
              type="datetime-local"
              value={to as string}
              onChange={(e) => handleFilterWithPageReset({ to: e.target.value })}
              aria-label="Filter to date and time"
            />
          </ToolbarField>

          <ToolbarField label="Status" htmlFor="audit-success">
            <Select
              id="audit-success"
              className="pf-control py-0"
              data-role="field"
              value={success as string}
              onChange={(e) => handleFilterWithPageReset({ success: e.target.value })}
              aria-label="Filter by login status"
            >
              <option value="">All attempts</option>
              <option value="true">Successful</option>
              <option value="false">Failed</option>
            </Select>
          </ToolbarField>
        </FiltersDisclosure>

        {hasActiveFilters && (
          <>
            <Toolbar.Spacer />
            <Button
              variant="ghost"
              control
              onClick={resetAll}
              iconLeft={<CloseIcon className="w-3.5 h-3.5" />}
              aria-label="Clear all filters"
            >
              Clear filters
            </Button>
          </>
        )}
      </Toolbar>

      {/* Table area */}
      {isLoading ? (
        <AdminLoading variant="table" cols={6} rows={8} label="Loading login audit entries" />
      ) : isError ? (
        <AdminError
          title="Failed to load login audit log"
          description="Something went wrong while fetching the audit entries. Retry, or check that the API is reachable."
          error={error}
          onRetry={() => { void refetch(); }}
        />
      ) : !data?.items.length ? (
        <AdminEmpty
          title="No login attempts found"
          description={hasActiveFilters ? 'Try adjusting your filters.' : 'No login attempts have been recorded yet.'}
        />
      ) : (
        <>
          {/* Summary + page-size selector */}
          <Toolbar aria-label="Login audit results controls" className="mb-3">
            <p
              className={clsx('text-sm text-pf-text-secondary', isFetching && 'opacity-60')}
              aria-live="polite"
              aria-atomic="true"
            >
              {isFetching
                ? 'Refreshing…'
                : `Showing ${rangeStart}–${rangeEnd} of ${totalCount.toLocaleString()} entries`}
            </p>

            <Toolbar.Spacer />
            {isFetching && <RefreshIcon className="w-3.5 h-3.5 pf-animate-spin text-pf-text-tertiary" aria-hidden />}
            <Select
              id="audit-page-size"
              className="pf-control py-0"
              data-role="field"
              value={String(currentPageSize)}
              onChange={(e) => setMany({ pageSize: Number(e.target.value), page: 1 } as Parameters<typeof setMany>[0])}
              containerClassName="w-28"
              aria-label="Rows per page"
            >
              {PAGE_SIZE_OPTIONS.map((n) => (
                <option key={n} value={String(n)}>
                  {n} per page
                </option>
              ))}
            </Select>
          </Toolbar>

          <div className="overflow-x-auto rounded-md border border-pf-border">
            <Table aria-label="Login audit log">
              <TableHead>
                <TableRow>
                  <TableHeaderCell scope="col">Timestamp</TableHeaderCell>
                  <TableHeaderCell scope="col">Username</TableHeaderCell>
                  <TableHeaderCell scope="col">Status</TableHeaderCell>
                  <TableHeaderCell scope="col" className="font-mono">IP Address</TableHeaderCell>
                  <TableHeaderCell scope="col">Failure Reason</TableHeaderCell>
                  <TableHeaderCell scope="col">User Agent</TableHeaderCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {data.items.map((entry) => (
                  <TableRow
                    key={entry.id}
                    className={clsx(!entry.success && 'bg-pf-error-bg/30')}
                  >
                    <TableCell>
                      <Tooltip content={entry.timestamp} position="right">
                        <span className="text-sm text-pf-text-primary whitespace-nowrap">
                          {formatLocalDateTime(entry.timestamp)}
                        </span>
                      </Tooltip>
                    </TableCell>

                    <TableCell>
                      <span className="text-sm text-pf-text-primary font-medium">
                        {entry.username ?? '—'}
                      </span>
                    </TableCell>

                    <TableCell>
                      {entry.success ? (
                        <Badge variant="success">
                          ✅ Success
                        </Badge>
                      ) : (
                        <Badge variant="error">
                          ❌ Failed
                        </Badge>
                      )}
                    </TableCell>

                    <TableCell>
                      <span className="font-mono text-sm text-pf-text-primary">
                        {entry.ipAddress || '—'}
                      </span>
                    </TableCell>

                    <TableCell>
                      <span className="text-sm text-pf-text-secondary">
                        {entry.failureReason ?? '—'}
                      </span>
                    </TableCell>

                    <TableCell>
                      {entry.userAgent ? (
                        <Tooltip content={entry.userAgent} position="left">
                          <span
                            className="text-sm text-pf-text-secondary font-mono cursor-default"
                            aria-label={`User agent: ${entry.userAgent}`}
                          >
                            {truncateUserAgent(entry.userAgent)}
                          </span>
                        </Tooltip>
                      ) : (
                        <span className="text-sm text-pf-text-tertiary">—</span>
                      )}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>

          {/* Pagination controls */}
          {totalPages > 1 && (
            <Pagination
              page={currentPage}
              totalPages={totalPages}
              onPageChange={(nextPage) => setMany({ page: nextPage } as Parameters<typeof setMany>[0])}
            />
          )}
        </>
      )}
    </PageTemplate>
  );
}
