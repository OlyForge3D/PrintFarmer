import { useState, useCallback, useEffect } from 'react';
import { apiClient } from '@/services/api';

export interface PushSubscriptionState {
  isSupported: boolean;
  isSubscribed: boolean;
  isLoading: boolean;
  error: string | null;
  subscribe: () => Promise<void>;
  unsubscribe: () => Promise<void>;
}

export function usePushSubscription(): PushSubscriptionState {
  const [isSubscribed, setIsSubscribed] = useState(false);
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const isSupported = typeof window !== 'undefined' &&
    'serviceWorker' in navigator &&
    'PushManager' in window &&
    'Notification' in window;

  // Sync isSubscribed with the browser's actual push subscription on mount
  useEffect(() => {
    if (!isSupported) return;

    let cancelled = false;
    navigator.serviceWorker.ready
      .then((registration) => registration.pushManager.getSubscription())
      .then((subscription) => {
        if (!cancelled) {
          setIsSubscribed(subscription !== null);
        }
      })
      .catch(() => {
        // Silently ignore — isSubscribed stays false
      });

    return () => { cancelled = true; };
  }, [isSupported]);

  const subscribe = useCallback(async () => {
    if (!isSupported) {
      setError('Push notifications are not supported in this browser.');
      return;
    }

    setIsLoading(true);
    setError(null);

    try {
      const permission = await Notification.requestPermission();
      if (permission !== 'granted') {
        setError('Notification permission denied.');
        setIsLoading(false);
        return;
      }

      const registration = await navigator.serviceWorker.ready;

      // Get VAPID public key from server
      const vapidKey = await apiClient.get('/notifications/push-subscription/vapid-key');
      if (!vapidKey.data.publicKey) {
        setError('Push notifications are not configured on this server. Contact your administrator to set up VAPID keys.');
        setIsLoading(false);
        return;
      }
      const applicationServerKey = urlBase64ToUint8Array(vapidKey.data.publicKey);

      const subscription = await registration.pushManager.subscribe({
        userVisibleOnly: true,
        applicationServerKey,
      });

      // Send subscription to backend
      await apiClient.post('/notifications/push-subscription', subscription.toJSON());
      setIsSubscribed(true);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to subscribe to push notifications.');
    } finally {
      setIsLoading(false);
    }
  }, [isSupported]);

  const unsubscribe = useCallback(async () => {
    setIsLoading(true);
    setError(null);

    try {
      const registration = await navigator.serviceWorker.ready;
      const subscription = await registration.pushManager.getSubscription();
      if (subscription) {
        const endpoint = subscription.endpoint;
        await subscription.unsubscribe();
        await apiClient.delete('/notifications/push-subscription', {
          data: { endpoint },
        });
      }
      setIsSubscribed(false);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to unsubscribe from push notifications.');
    } finally {
      setIsLoading(false);
    }
  }, []);

  return { isSupported, isSubscribed, isLoading, error, subscribe, unsubscribe };
}

function urlBase64ToUint8Array(base64String: string): Uint8Array {
  const padding = '='.repeat((4 - (base64String.length % 4)) % 4);
  const base64 = (base64String + padding).replace(/-/g, '+').replace(/_/g, '/');
  const rawData = window.atob(base64);
  const outputArray = new Uint8Array(rawData.length);
  for (let i = 0; i < rawData.length; ++i) {
    outputArray[i] = rawData.charCodeAt(i);
  }
  return outputArray;
}
