import { PageTemplate } from '@/common/components/PageTemplate';
import { ProtectedRoute } from '@/features/auth/components/ProtectedRoute';

export function AdminPage() {
  return (
    <ProtectedRoute requiredRole="farm_admin">
      <PageTemplate title="Administration" subtitle="Administration links moved to primary navigation">
        <div className="py-8">
          <p className="text-pf-text-secondary">Administration items like Printers have been moved into the main navigation. Use the primary nav to access admin tools.</p>
        </div>
      </PageTemplate>
    </ProtectedRoute>
  );
}
