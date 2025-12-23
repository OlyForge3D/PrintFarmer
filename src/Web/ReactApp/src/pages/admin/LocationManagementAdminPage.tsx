import { LocationManagement } from '@/components/LocationManagement';

export function LocationManagementAdminPage() {
  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-3xl font-bold text-pf-text-primary">Location Management</h1>
        <p className="text-pf-text-secondary mt-2">
          Create, edit, and organize printer locations
        </p>
      </div>
      <LocationManagement />
    </div>
  );
}

export default LocationManagementAdminPage;
