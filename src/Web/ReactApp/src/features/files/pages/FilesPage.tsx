import { SubMenuLayout } from '@/common/components/SubMenuLayout';
import { CubeIcon, FileIcon } from '@/common/components/icons/MdiIcons';
import { ModelsPage } from '@/features/models3d/pages/ModelsPage';
import { GcodeLibraryPage } from '@/features/gcode/pages/GcodeLibraryPage';
import { JobQueueDashboardPage } from '@/features/queue/pages/JobQueueDashboardPage';
import { HarvestPage } from '@/features/gcode/pages/HarvestPage';
import { useLocation } from 'react-router-dom';

const fileMenuItems = [
  { name: 'Models', href: '/files/models', icon: CubeIcon },
  { name: 'G-code', href: '/files/library', icon: FileIcon },
  { name: 'Slice Jobs', href: '/files/jobs', icon: FileIcon },
  { name: 'Harvest', href: '/files/harvest', icon: FileIcon },
];

export function FilesPage() {
  const location = useLocation();
  const path = location.pathname;

  let content;
  if (path === '/files/library') {
    content = <GcodeLibraryPage />;
  } else if (path === '/files/jobs') {
    content = <JobQueueDashboardPage />;
  } else if (path.startsWith('/files/harvest')) {
    content = <HarvestPage />;
  } else if (path === '/files/models') {
    content = <ModelsPage />;
  } else {
    content = <ModelsPage />;
  }

  return (
    <SubMenuLayout title="Files" items={fileMenuItems}>
      {content}
    </SubMenuLayout>
  );
}
