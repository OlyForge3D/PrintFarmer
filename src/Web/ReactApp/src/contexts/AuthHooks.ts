import { useAuthInternal } from './AuthContext';

// Public hook re-exported from dedicated file to keep AuthContext file focused on provider/component exports.
export function useAuth() {
  return useAuthInternal();
}
