import { Navigate } from 'react-router';

/**
 * SliceJobsPage now redirects to Settings > Admin > Operations > Workers > Jobs.
 * The slice jobs UI has been consolidated into WorkerManagementPage.
 */
export function SliceJobsPage() {
  return <Navigate to="/settings?scope=admin&tab=operations&sub=workers&workerTab=jobs" replace />;
}

export default SliceJobsPage;
