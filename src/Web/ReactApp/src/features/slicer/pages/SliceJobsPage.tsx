import { Navigate } from 'react-router';

/**
 * SliceJobsPage now redirects to the Worker Management page Jobs tab.
 * The slice jobs UI has been consolidated into WorkerManagementPage.
 */
export function SliceJobsPage() {
  return <Navigate to="/admin/workers?tab=jobs" replace />;
}

export default SliceJobsPage;
