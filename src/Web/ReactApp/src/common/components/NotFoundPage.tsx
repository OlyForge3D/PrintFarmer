import { useNavigate } from 'react-router';
import { Button } from '@/common/components/ui';
import { HomeIcon } from '@/common/components/icons/MdiIcons';

export function NotFoundPage() {
  const navigate = useNavigate();

  return (
    <div className="flex flex-col items-center justify-center min-h-[60vh] px-6 text-center">
      <div className="text-6xl font-bold text-pf-text-tertiary mb-2">404</div>
      <h1 className="text-xl font-semibold text-pf-text-primary mb-2">
        Page not found
      </h1>
      <p className="text-sm text-pf-text-secondary mb-6 max-w-sm">
        We couldn't find the page you're looking for. It may have been moved or doesn't exist.
      </p>
      <Button
        variant="primary"
        onClick={() => navigate('/dashboard')}
        iconLeft={<HomeIcon className="h-4 w-4" />}
      >
        Go to Dashboard
      </Button>
    </div>
  );
}
