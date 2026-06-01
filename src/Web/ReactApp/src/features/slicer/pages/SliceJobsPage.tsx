import { Navigate } from 'react-router';

/**
 * SliceJobsPage now redirects to Settings > System > Workers > Jobs.
 * The slice jobs UI has been consolidated into WorkerManagementPage.
 */
export function SliceJobsPage() {
  return <Navigate to="/settings?tab=system&sub=workers&workerTab=jobs" replace />;
}

export default SliceJobsPage;
