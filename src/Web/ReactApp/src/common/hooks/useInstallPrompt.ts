import { useState, useEffect } from 'react';

interface BeforeInstallPromptEvent extends Event {
  prompt: () => Promise<void>;
  userChoice: Promise<{ outcome: 'accepted' | 'dismissed' }>;
}

export function useInstallPrompt() {
  const [installPrompt, setInstallPrompt] = useState<BeforeInstallPromptEvent | null>(null);
  const [isDismissed, setIsDismissed] = useState<boolean>(() => {
    // Check if dismissed in the last 7 days
    const dismissed = localStorage.getItem('pwa-install-dismissed');
    if (dismissed) {
      const dismissedDate = new Date(dismissed);
      const weekAgo = new Date();
      weekAgo.setDate(weekAgo.getDate() - 7);
      return dismissedDate > weekAgo;
    }
    return false;
  });

  useEffect(() => {
    const handler = (e: Event) => {
      e.preventDefault();
      setInstallPrompt(e as BeforeInstallPromptEvent);
    };

    window.addEventListener('beforeinstallprompt', handler);

    return () => {
      window.removeEventListener('beforeinstallprompt', handler);
    };
  }, []);

  const promptInstall = async () => {
    if (!installPrompt) return false;

    await installPrompt.prompt();
    const { outcome } = await installPrompt.userChoice;

    if (outcome === 'accepted') {
      setInstallPrompt(null);
      return true;
    } else {
      dismiss();
      return false;
    }
  };

  const dismiss = () => {
    localStorage.setItem('pwa-install-dismissed', new Date().toISOString());
    setIsDismissed(true);
  };

  return {
    canInstall: !!installPrompt && !isDismissed,
    promptInstall,
    dismiss,
  };
}
