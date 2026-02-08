import { useState } from 'react';
import { useAuth } from '@/features/auth/hooks/useAuth';
import { apiClient } from '@/services/api';
import { toast } from 'sonner';
import { CloseIcon, EmailIcon, RefreshIcon } from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui';

export function EmailConfirmationBanner() {
  const { user } = useAuth();
  const [sending, setSending] = useState(false);
  const [dismissed, setDismissed] = useState(false);

  // Don't show if user is not logged in, email is confirmed, or banner was dismissed
  if (!user || user.emailConfirmed || dismissed) {
    return null;
  }

  const handleResend = async () => {
    setSending(true);
    try {
      const result = await apiClient.resendEmailConfirmation();
      if (result.success) {
        toast.success(result.message || 'Confirmation email sent!');
      } else {
        toast.error(result.message || 'Failed to send confirmation email');
      }
    } catch (error: unknown) {
      const errorMessage = (error as { response?: { data?: { message?: string } }; message?: string })?.response?.data?.message ||
                          (error as { message?: string })?.message ||
                          'An error occurred while sending confirmation email';
      toast.error(errorMessage);
    } finally {
      setSending(false);
    }
  };

  return (
    <div className="border-b border-pf-border" style={{ backgroundColor: 'var(--pf-warning-bg)' }}>
      <div className="max-w-7xl mx-auto py-3 px-4 sm:px-6 lg:px-8">
        <div className="flex items-center justify-between flex-wrap">
          <div className="flex items-center flex-1">
            <span className="flex p-2 rounded-lg bg-pf-bg-2">
              <EmailIcon className="h-5 w-5" style={{ color: 'var(--pf-warning)' }} />
            </span>
            <p className="ml-3 font-medium text-pf-text-primary">
              <span className="md:hidden">Please verify your email</span>
              <span className="hidden md:inline">
                Please verify your email address to access all features
              </span>
            </p>
          </div>
          <div className="order-3 mt-2 shrink-0 w-full sm:order-2 sm:mt-0 sm:w-auto space-x-2 flex items-center">
            <Button
              variant="subtle"
              onClick={handleResend}
              disabled={sending}
              className="flex items-center justify-center bg-pf-bg-2 hover:bg-pf-border"
              style={{ color: 'var(--pf-warning)' }}
              iconLeft={sending ? <RefreshIcon className="h-4 w-4" /> : <EmailIcon className="h-4 w-4" />}
            >
              {sending ? 'Sending...' : 'Resend Email'}
            </Button>
            <Button
              variant="subtle"
              size="sm"
              onClick={() => setDismissed(true)}
              aria-label="Dismiss"
              className="p-1 text-pf-warning"
            >
              <CloseIcon className="h-5 w-5" />
            </Button>
          </div>
        </div>
      </div>
    </div>
  );
}
