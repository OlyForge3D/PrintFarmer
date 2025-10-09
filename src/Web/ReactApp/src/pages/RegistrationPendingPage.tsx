import React from 'react';
import { Link } from 'react-router-dom';

const RegistrationPendingPage: React.FC = () => {
  return (
    <div className="flex flex-col items-center justify-center min-h-screen bg-gray-50">
      <div className="bg-white p-8 rounded shadow-md max-w-md w-full text-center">
        <h1 className="text-2xl font-bold mb-4">Registration Submitted</h1>
        <p className="mb-6 text-gray-700">
          Your account has been created, but you cannot access PrintFarmer until an administrator approves your registration.<br />
          You will be notified once your account is approved.
        </p>
        <Link to="/login" className="text-blue-600 hover:underline">Back to Login</Link>
      </div>
    </div>
  );
};

export default RegistrationPendingPage;
