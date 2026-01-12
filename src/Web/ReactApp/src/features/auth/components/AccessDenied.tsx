import React from 'react';
import { ExclamationTriangleIcon } from '@heroicons/react/24/outline';

export const AccessDenied: React.FC = () => {
  return (
    <div className="min-h-screen flex items-center justify-center bg-pf-bg-0">
      <div className="max-w-md w-full bg-pf-bg-1 shadow-lg rounded-lg p-6 text-center border border-pf-border">
        <ExclamationTriangleIcon className="mx-auto h-12 w-12" style={{ color: 'var(--pf-error)' }} />
        <h1 className="mt-4 text-xl font-semibold text-pf-text-primary">Access Denied</h1>
        <p className="mt-2 text-sm text-pf-text-secondary">
          You don't have permission to access this resource. Please contact your administrator if you believe this is an error.
        </p>
      </div>
    </div>
  );
};