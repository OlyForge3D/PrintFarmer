import { useState } from 'react';
import { useAuth } from '@/contexts/AuthHooks';
import { apiClient } from '@/services/api';
import { toast } from 'sonner';
import { Mail, X, RefreshCw } from 'lucide-react';

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
    <div className="bg-yellow-500/10 border-b border-yellow-500/20">
      <div className="max-w-7xl mx-auto py-3 px-4 sm:px-6 lg:px-8">
        <div className="flex items-center justify-between flex-wrap">
          <div className="flex items-center flex-1">
            <span className="flex p-2 rounded-lg bg-yellow-500/20">
              <Mail className="h-5 w-5 text-yellow-600" aria-hidden="true" />
            </span>
            <p className="ml-3 font-medium text-yellow-800 dark:text-yellow-200">
              <span className="md:hidden">Please verify your email</span>
              <span className="hidden md:inline">
                Please verify your email address to access all features
              </span>
            </p>
          </div>
          <div className="order-3 mt-2 flex-shrink-0 w-full sm:order-2 sm:mt-0 sm:w-auto space-x-2 flex items-center">
            <button
              onClick={handleResend}
              disabled={sending}
              className="flex items-center justify-center px-4 py-2 border border-transparent rounded-md shadow-sm text-sm font-medium text-yellow-900 bg-yellow-100 hover:bg-yellow-200 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-yellow-500 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
            >
              {sending ? (
                <>
                  <RefreshCw className="animate-spin h-4 w-4 mr-2" />
                  Sending...
                </>
              ) : (
                <>
                  <Mail className="h-4 w-4 mr-2" />
                  Resend Email
                </>
              )}
            </button>
            <button
              onClick={() => setDismissed(true)}
              className="flex items-center justify-center p-2 rounded-md hover:bg-yellow-500/10 focus:outline-none focus:ring-2 focus:ring-offset-2 focus:ring-yellow-500 transition-colors"
              aria-label="Dismiss"
            >
              <X className="h-5 w-5 text-yellow-600" />
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}
