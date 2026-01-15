import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '@/services/api';

export interface PasswordPolicy {
  minLength: number;
  requireUppercase: boolean;
  requireLowercase: boolean;
  requireDigit: boolean;
  requireSymbol: boolean;
}

const QUERY_KEY = ['passwordPolicy'];

async function fetchPolicy(): Promise<PasswordPolicy> {
  const resp = await apiClient.getPasswordPolicy();
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
  const resp = await apiClient.updatePasswordPolicy(policy as unknown as Record<string, unknown>);
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
