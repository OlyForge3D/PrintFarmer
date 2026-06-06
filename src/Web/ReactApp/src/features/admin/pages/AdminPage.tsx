import { ProtectedRoute } from '@/features/auth/components/ProtectedRoute';
import { Navigate } from 'react-router';

export function AdminPage() {
  return (
    <ProtectedRoute requiredRole="farm_admin">
      <Navigate to="/settings?scope=admin" replace />
    </ProtectedRoute>
  );
}
