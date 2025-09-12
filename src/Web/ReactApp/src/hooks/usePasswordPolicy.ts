import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';

export interface PasswordPolicy {
  minLength: number;
  requireUppercase: boolean;
  requireLowercase: boolean;
  requireDigit: boolean;
  requireSymbol: boolean;
}

const QUERY_KEY = ['passwordPolicy'];

function authHeader(): Record<string, string> {
  const token = localStorage.getItem('auth-token');
  return token ? { Authorization: `Bearer ${token}` } : {} as Record<string, string>;
}

async function fetchPolicy(): Promise<PasswordPolicy> {
  const resp = await fetch('/api/settings/security/password-policy', { headers: authHeader() });
  if (!resp.ok) {
    throw new Error(`Failed to load password policy (HTTP ${resp.status})`);
  }
  const data = await resp.json();
  return {
    minLength: data.minLength ?? 12,
    requireUppercase: !!data.requireUppercase,
    requireLowercase: !!data.requireLowercase,
    requireDigit: !!data.requireDigit,
    requireSymbol: !!data.requireSymbol
  };
}

async function updatePolicy(policy: PasswordPolicy): Promise<PasswordPolicy> {
  const resp = await fetch('/api/settings/security/password-policy', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json', ...authHeader() },
    body: JSON.stringify(policy)
  });
  if (!resp.ok) {
    throw new Error(`Failed to save password policy (HTTP ${resp.status})`);
  }
  const data = await resp.json();
  return {
    minLength: data.minLength,
    requireUppercase: data.requireUppercase,
    requireLowercase: data.requireLowercase,
    requireDigit: data.requireDigit,
    requireSymbol: data.requireSymbol
  };
}

export function usePasswordPolicy() {
  const queryClient = useQueryClient();

  const query = useQuery<PasswordPolicy>({
    queryKey: QUERY_KEY,
    queryFn: fetchPolicy,
    staleTime: 5 * 60 * 1000,
    refetchOnWindowFocus: false
  });

  const mutation = useMutation({
    mutationFn: updatePolicy,
    onSuccess: (data) => {
      queryClient.setQueryData(QUERY_KEY, data);
    }
  });

  return {
    ...query,
    savePolicy: mutation.mutateAsync,
    saving: mutation.isPending,
    errorSaving: mutation.isError,
    reset: () => queryClient.invalidateQueries({ queryKey: QUERY_KEY })
  };
}
