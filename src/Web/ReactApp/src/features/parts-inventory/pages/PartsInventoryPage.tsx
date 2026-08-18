/**
 * PartsInventoryPage — desktop configuration surface for printed-part
 * stock (F9 / issue #721). Provides tabs for:
 *
 *   • SKUs        — CRUD for printed-part SKUs and their default bin.
 *   • Bins        — CRUD/register storage bins with barcode labels.
 *   • Mappings    — output → SKU mappings, including multi-SKU plates.
 *   • Reorder     — SKUs at or below reorder point.
 *
 * Printed parts are the physical outputs of prints. They are *separate*
 * from the maintenance-component inventory (`/maintenance` → Inventory
 * tab), which tracks replacement parts used to service printers. This
 * page never touches the maintenance API surface.
 */

import { useCallback, useEffect, useMemo } from 'react';
import { useNavigate, useParams } from 'react-router';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Tabs, Badge, Alert, Spinner } from '@/common/components/ui';
import {
  PackageIcon,
  DatabaseIcon,
  AlertIcon,
  LayersIcon,
} from '@/common/components/icons/MdiIcons';
import { PartsTab } from '../components/PartsTab';
import { BinsTab } from '../components/BinsTab';
import { MappingsTab } from '../components/MappingsTab';
import { ReorderTab } from '../components/ReorderTab';
import { useReorderCandidates } from '../hooks/usePartsInventory';
import { isFeatureDisabledError } from '../utils/problemDetails';

const TAB_IDS = ['skus', 'bins', 'mappings', 'reorder'] as const;
type TabId = (typeof TAB_IDS)[number];

const STORAGE_KEY = 'pf.partsInventory.activeTab';
const DEFAULT_TAB: TabId = 'skus';

function isTabId(value: string | undefined | null): value is TabId {
  return typeof value === 'string' && (TAB_IDS as readonly string[]).includes(value);
}

function readSavedTab(): TabId {
  if (typeof window === 'undefined') return DEFAULT_TAB;
  const saved = window.localStorage.getItem(STORAGE_KEY);
  return isTabId(saved) ? saved : DEFAULT_TAB;
}

export function PartsInventoryPage() {
  const navigate = useNavigate();
  const { tabId } = useParams<{ tabId?: string }>();

  // URL is the single source of truth; if none supplied fall back to saved / default.
  const activeTab: TabId = isTabId(tabId) ? tabId : readSavedTab();

  // If no tabId in URL, redirect to the resolved default so the URL always matches state.
  useEffect(() => {
    if (!isTabId(tabId)) {
      navigate(`/parts-inventory/${activeTab}`, { replace: true });
    }
  }, [tabId, activeTab, navigate]);

  // Persist active tab whenever the URL changes to a valid tab.
  useEffect(() => {
    if (isTabId(tabId) && typeof window !== 'undefined') {
      window.localStorage.setItem(STORAGE_KEY, tabId);
    }
  }, [tabId]);

  const handleTabChange = useCallback(
    (nextId: string) => {
      if (!isTabId(nextId)) return;
      navigate(`/parts-inventory/${nextId}`, { replace: true });
    },
    [navigate]
  );

  const {
    data: reorderCandidates = [],
    error: reorderError,
    isLoading: reorderStatusLoading,
  } = useReorderCandidates();
  const reorderCount = reorderCandidates.length;

  // The parts API root paths return a `featureDisabled` 404 when an admin has
  // switched the feature off. The reorder query hits one of those root paths on
  // every page mount and is used purely as a status probe, so it surfaces the
  // disabled state regardless of the active tab.
  const featureDisabled = isFeatureDisabledError(reorderError);

  // Until the probe settles we don't yet know whether the feature is
  // enabled. Rendering the tabs before then would mount the default (SKUs)
  // tab, whose own `useParts`/`useBins` queries would fire in parallel with
  // the probe — and get their own 404s once the feature turns out to be
  // disabled (issue #1686). Withhold the tab content until the probe query
  // has settled, one way or the other.
  const featureStatusKnown = !reorderStatusLoading;

  const subtitle = useMemo(
    () =>
      'Configure printed-part SKUs, storage bins with barcodes, and job-output mappings. Separate from maintenance components.',
    []
  );

  return (
    <PageTemplate
      title="Printed Parts Inventory"
      subtitle={subtitle}
      icon={PackageIcon}
    >
      {!featureStatusKnown ? (
        <div className="flex items-center gap-2 py-8 justify-center text-pf-text-secondary">
          <Spinner size="md" />
          <span>Checking printed-parts inventory status…</span>
        </div>
      ) : featureDisabled ? (
        <Alert type="warning">
          The parts inventory feature is currently disabled by an administrator
        </Alert>
      ) : (
        <Tabs activeTab={activeTab} onTabChange={handleTabChange} className="space-y-0">
        <Tabs.List
          className="border-b border-pf-border bg-pf-bg-1 -mx-4 px-4 mb-0 overflow-x-auto"
          aria-label="Printed parts inventory sections"
        >
          <Tabs.Tab id="skus" icon={<PackageIcon className="h-4 w-4" ariaLabel="SKUs" />}>
            SKUs
          </Tabs.Tab>
          <Tabs.Tab id="bins" icon={<DatabaseIcon className="h-4 w-4" ariaLabel="Bins" />}>
            Bins
          </Tabs.Tab>
          <Tabs.Tab id="mappings" icon={<LayersIcon className="h-4 w-4" ariaLabel="Mappings" />}>
            Output Mappings
          </Tabs.Tab>
          <Tabs.Tab
            id="reorder"
            icon={<AlertIcon className="h-4 w-4" ariaLabel="Reorder" />}
          >
            <span className="inline-flex items-center gap-2">
              Reorder
              {reorderCount > 0 && (
                <Badge variant="warning" size="sm">
                  {reorderCount}
                </Badge>
              )}
            </span>
          </Tabs.Tab>
        </Tabs.List>

        <Tabs.Panels>
          <Tabs.Panel id="skus">
            <div className="mt-4">
              <PartsTab />
            </div>
          </Tabs.Panel>
          <Tabs.Panel id="bins">
            <div className="mt-4">
              <BinsTab />
            </div>
          </Tabs.Panel>
          <Tabs.Panel id="mappings">
            <div className="mt-4">
              <MappingsTab />
            </div>
          </Tabs.Panel>
          <Tabs.Panel id="reorder">
            <div className="mt-4">
              <ReorderTab onOpenSkusTab={() => handleTabChange('skus')} />
            </div>
          </Tabs.Panel>
        </Tabs.Panels>
      </Tabs>
      )}
    </PageTemplate>
  );
}

export default PartsInventoryPage;
