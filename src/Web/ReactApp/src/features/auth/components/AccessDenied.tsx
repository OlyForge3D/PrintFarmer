import React from 'react';
import { ExclamationTriangleIcon } from '@heroicons/react/24/outline';

export const AccessDenied: React.FC = () => {
  return (
    <div className="min-h-screen flex items-center justify-center bg-gray-50">
      <div className="max-w-md w-full bg-white shadow-lg rounded-lg p-6 text-center">
        <ExclamationTriangleIcon className="mx-auto h-12 w-12 text-red-500" />
        <h1 className="mt-4 text-xl font-semibold text-gray-900">Access Denied</h1>
        <p className="mt-2 text-sm text-gray-600">
          You don't have permission to access this resource. Please contact your administrator if you believe this is an error.
        </p>
      </div>
    </div>
  );
};