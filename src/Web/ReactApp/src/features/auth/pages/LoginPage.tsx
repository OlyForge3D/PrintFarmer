import React, { useState } from 'react';
import { useNavigate } from 'react-router';
import { LoginModal } from '@/features/auth/components/LoginModal';
import { RegisterModal } from '@/features/auth/components/RegisterModal';

export function LoginPage() {
  const [showRegister, setShowRegister] = useState(false);
  const navigate = useNavigate();

  return (
    <div className="min-h-screen flex items-center justify-center bg-pf-bg-0">
      <LoginModal
        isOpen={!showRegister}
        onClose={() => navigate('/')}
        onSwitchToRegister={() => setShowRegister(true)}
      />
      <RegisterModal
        isOpen={showRegister}
        onClose={() => setShowRegister(false)}
        onSwitchToLogin={() => setShowRegister(false)}
      />
    </div>
  );
}
