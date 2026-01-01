import { PageTemplate } from '@/common/components/PageTemplate';
import { LocationManagement } from '@/features/catalog/components/LocationManagement';
import { LayersIcon } from '@/common/components/icons/MdiIcons';

export function LocationManagementAdminPage() {
  return (
    <PageTemplate
      title="Location Management"
      subtitle="Create, edit, and organize printer locations"
      icon={LayersIcon}
    >
      <LocationManagement />
    </PageTemplate>

  );
}

export default LocationManagementAdminPage;
