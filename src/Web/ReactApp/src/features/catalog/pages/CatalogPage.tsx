import { useState } from 'react';
import { DatabaseIcon, PrinterIcon, ThermometerIcon, SettingsIcon, WrenchIcon, CircleIcon } from '@/common/components/icons/MdiIcons';
import { PageTemplate } from '@/common/components/PageTemplate';
import { Tabs } from '@/common/components/ui/Tabs';
import { PrinterModelsCatalog } from '@/features/catalog/components/PrinterModelsCatalog';
import { HotendsCatalog } from '@/features/catalog/components/HotendsCatalog';
import { ExtrudersCatalog } from '@/features/catalog/components/ExtrudersCatalog';
import { ToolheadsCatalog } from '@/features/catalog/components/ToolheadsCatalog';
import { NozzlesCatalog } from '@/features/catalog/components/NozzlesCatalog';

type CatalogTab = 'printers' | 'hotends' | 'extruders' | 'toolheads' | 'nozzles';

/**
 * CatalogPage - Tabbed catalog management page
 * 
 * Provides tabs for managing different catalog types:
 * - Printers: Manufacturers and printer models
 * - Hotends: Hotend models and specifications
 * - Extruders: Extruder models and specifications  
 * - Toolheads: Toolhead models
 * - Nozzles: Nozzle models and specifications
 */
export function CatalogPage() {
  const [activeTab, setActiveTab] = useState<CatalogTab>('printers');

  return (
    <PageTemplate
      title="Catalog"
      subtitle="Manage printer manufacturers, models, and components"
      icon={DatabaseIcon}
    >
      <Tabs
        activeTab={activeTab}
        onTabChange={(id) => setActiveTab(id as CatalogTab)}
        className="h-full"
      >
        <Tabs.List>
          <Tabs.Tab id="printers" icon={<PrinterIcon className="h-4 w-4" />}>
            Printers
          </Tabs.Tab>
          <Tabs.Tab id="toolheads" icon={<WrenchIcon className="h-4 w-4" />}>
            Toolheads
          </Tabs.Tab>
          <Tabs.Tab id="extruders" icon={<SettingsIcon className="h-4 w-4" />}>
            Extruders
          </Tabs.Tab>
          <Tabs.Tab id="hotends" icon={<ThermometerIcon className="h-4 w-4" />}>
            Hotends
          </Tabs.Tab>
          <Tabs.Tab id="nozzles" icon={<CircleIcon className="h-4 w-4" />}>
            Nozzles
          </Tabs.Tab>
        </Tabs.List>
        <Tabs.Panels className="min-h-[600px]">
          <Tabs.Panel id="printers">
            <PrinterModelsCatalog />
          </Tabs.Panel>
          <Tabs.Panel id="toolheads">
            <ToolheadsCatalog />
          </Tabs.Panel>
          <Tabs.Panel id="extruders">
            <ExtrudersCatalog />
          </Tabs.Panel>
          <Tabs.Panel id="hotends">
            <HotendsCatalog />
          </Tabs.Panel>
          <Tabs.Panel id="nozzles">
            <NozzlesCatalog />
          </Tabs.Panel>
        </Tabs.Panels>
      </Tabs>
    </PageTemplate>
  );
}
