import React, { useState } from 'react';
import { useNavigate } from 'react-router';
import { LoginModal } from '@/features/auth/components/LoginModal';
import { RegisterModal } from '@/features/auth/components/RegisterModal';

export function LoginPage() {
  const [showRegister, setShowRegister] = useState(false);
  const navigate = useNavigate();

  return (
    <main className="min-h-screen bg-pf-bg-0 px-4 py-10 sm:px-6 lg:px-8">
      <div className="mx-auto flex min-h-[calc(100vh-5rem)] items-center justify-center">
        <LoginModal
          isOpen={!showRegister}
          onClose={() => navigate('/')}
          onSwitchToRegister={() => setShowRegister(true)}
          surface="page"
        />
        <RegisterModal
          isOpen={showRegister}
          onClose={() => setShowRegister(false)}
          onSwitchToLogin={() => setShowRegister(false)}
          surface="page"
        />
      </div>
    </main>
  );
}
