import React, { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { LoginModal } from '@/components/auth/LoginModal';
import { RegisterModal } from '@/components/auth/RegisterModal';

export default function LoginPage() {
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
