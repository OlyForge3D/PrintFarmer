import { useState } from 'react';
import { useLocation, useNavigate } from 'react-router';
import { LoginModal } from '@/features/auth/components/LoginModal';
import { RegisterModal } from '@/features/auth/components/RegisterModal';

function getSafeReturnPath(state: unknown): string {
  if (!state || typeof state !== 'object') return '/';

  const from = (state as { from?: unknown }).from;
  if (!from || typeof from !== 'object') return '/';

  const { pathname, search } = from as { pathname?: unknown; search?: unknown };
  if (typeof pathname !== 'string' || !pathname.startsWith('/')) return '/';
  if (search !== undefined && (typeof search !== 'string' || (search !== '' && !search.startsWith('?')))) {
    return '/';
  }

  try {
    const destination = new URL(`${pathname}${search ?? ''}`, window.location.origin);
    if (destination.origin !== window.location.origin) return '/';
    return `${destination.pathname}${destination.search}`;
  } catch {
    return '/';
  }
}

export function LoginPage() {
  const [showRegister, setShowRegister] = useState(false);
  const navigate = useNavigate();
  const location = useLocation();
  const returnPath = getSafeReturnPath(location.state);

  return (
    <main className="min-h-screen bg-pf-bg-0 px-4 py-10 sm:px-6 lg:px-8">
      <div className="mx-auto flex min-h-[calc(100vh-5rem)] items-center justify-center">
        <LoginModal
          isOpen={!showRegister}
          onClose={() => navigate('/')}
          onLoginSuccess={() => navigate(returnPath, { replace: true })}
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
