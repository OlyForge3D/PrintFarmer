import React from 'react';
import { Link } from 'react-router';

export const RegistrationPendingPage: React.FC = () => {
  return (
    <div className="flex flex-col items-center justify-center min-h-screen bg-pf-bg-1">
      <div className="bg-pf-bg-0 p-8 rounded-sm shadow-md max-w-md w-full text-center">
        <h1 className="text-2xl font-bold mb-4">Registration Submitted</h1>
        <p className="mb-6 text-pf-text-primary">
          Your account has been created, but you cannot access PrintFarmer until an administrator approves your registration.<br />
          You will be notified once your account is approved.
        </p>
        <Link to="/login" className="text-pf-accent hover:underline">Back to Login</Link>
      </div>
    </div>
  );
};

export default RegistrationPendingPage;
