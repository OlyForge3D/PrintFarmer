import { SubMenuLayout } from '@/common/components/SubMenuLayout';
import { PrinterIcon } from '@/common/components/icons/MdiIcons';
import { useLocation } from 'react-router-dom';
import { ProtectedRoute } from '@/features/auth/components/ProtectedRoute';
import { PrintersAdminPage } from '@/features/printers/pages/admin/PrintersAdminPage';
import SlicerJobStatus from '@/features/slicer/components/SlicerJobStatus';

const adminMenuItems = [
  { name: 'Printers', href: '/admin/printers', icon: PrinterIcon },
];

export function AdminPage() {
  const location = useLocation();
  const path = location.pathname;

  let content;
  
  if (path === '/admin/printers') {
    content = <ProtectedRoute requiredRole="farm_admin"><PrintersAdminPage /></ProtectedRoute>;
  } else {
    content = <PrintersAdminPage />;
  }

  return <SubMenuLayout title="Administration" items={adminMenuItems}>{content}</SubMenuLayout>;
}
