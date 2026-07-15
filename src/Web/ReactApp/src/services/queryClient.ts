import { QueryClient } from '@tanstack/react-query';

// Shared React Query client singleton.
//
// This lives outside App.tsx (rather than being constructed inline) so it can
// be imported directly by modules that sit above <QueryClientProvider> in the
// tree — most notably AuthContext, which must be able to clear user-owned
// cache entries on logout/login without relying on the useQueryClient() hook.
// See src/common/auth/sensitiveQueryCache.ts (#762).
export const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      retry: (failureCount, error: unknown) => {
        // Don't retry client (4xx) errors
        const statusCode = typeof error === 'object' && error && 'statusCode' in error
          ? (error as { statusCode?: number }).statusCode
          : undefined;
        if (typeof statusCode === 'number' && statusCode >= 400 && statusCode < 500) {
          return false;
        }
        return failureCount < 3; // retry other errors up to 3 times
      },
      staleTime: 30000, // 30 seconds
      gcTime: 300000, // 5 minutes
    },
    mutations: {
      retry: false, // Don't retry mutations by default
    },
  },
});
