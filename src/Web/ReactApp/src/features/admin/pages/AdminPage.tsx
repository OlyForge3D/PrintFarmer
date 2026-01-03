import { SubMenuLayout } from '@/common/components/SubMenuLayout';
import { PrinterIcon, LayersIcon, FileIcon, GearIcon } from '@/common/components/icons/MdiIcons';
import { useLocation } from 'react-router-dom';
import { ProtectedRoute } from '@/features/auth/components/ProtectedRoute';
import { PrintersAdminPage } from '@/features/printers/pages/admin/PrintersAdminPage';
import { WorkerManagementPage } from '@/features/slicer/pages/WorkerManagementPage';
import { FileHealthDashboard } from '@/features/gcode/components/file-health/FileHealthDashboard';
import { TagAdminPage } from './TagAdminPage';
import { ObservabilityDashboard } from '@/common/components/ObservabilityDashboard';
import { SlicerDryRunPage } from '@/features/slicer/pages/SlicerDryRunPage';
import { SlicerJobStatusPage } from '@/features/slicer/pages/SlicerJobStatusPage';
import { SlicerProfilesPage } from '@/features/slicer/pages/SlicerProfilesPage';

const adminMenuItems = [
  { name: 'Printers', href: '/admin/printers', icon: PrinterIcon },
  { name: 'Workers', href: '/admin/workers', icon: GearIcon },
  { name: 'File Health', href: '/admin/file-health', icon: FileIcon },
  { name: 'Observability', href: '/admin/observability', icon: GearIcon },
  { name: 'Slicer Dry Run', href: '/admin/slicer/dry-run', icon: FileIcon },
  { name: 'Slicer Job Status', href: '/admin/slicer/job-status', icon: FileIcon },
  { name: 'Slicer Profiles', href: '/admin/slicer-profiles', icon: FileIcon },
  { name: 'Tags', href: '/admin/tags', icon: LayersIcon },
];

export function AdminPage() {
  const location = useLocation();
  const path = location.pathname;

  let content;
  
  if (path === '/admin/printers') {
    content = <ProtectedRoute requiredRole="farm_admin"><PrintersAdminPage /></ProtectedRoute>;
  } else if (path === '/admin/workers') {
    content = <ProtectedRoute requiredRole="farm_admin"><WorkerManagementPage /></ProtectedRoute>;
  } else if (path === '/admin/file-health') {
    content = <ProtectedRoute requiredRole="farm_admin"><FileHealthDashboard /></ProtectedRoute>;
  } else if (path === '/admin/observability') {
    content = <ProtectedRoute requiredRole="farm_admin"><ObservabilityDashboard /></ProtectedRoute>;
  } else if (path === '/admin/slicer/dry-run') {
    content = <ProtectedRoute requiredRole="farm_admin"><SlicerDryRunPage /></ProtectedRoute>;
  } else if (path === '/admin/slicer/job-status') {
    content = <ProtectedRoute requiredRole="farm_admin"><SlicerJobStatusPage /></ProtectedRoute>;
  } else if (path === '/admin/slicer-profiles') {
    content = <ProtectedRoute requiredRole="farm_admin"><SlicerProfilesPage /></ProtectedRoute>;
  } else if (path === '/admin/tags') {
    content = <ProtectedRoute requiredRole="farm_admin"><TagAdminPage /></ProtectedRoute>;
  } else {
    content = <PrintersAdminPage />;
  }

  return <SubMenuLayout title="Administration" items={adminMenuItems}>{content}</SubMenuLayout>;
}
