import { useQuery } from '@tanstack/react-query';
import { fetchLoginAudit } from '@/services/securityAuditService';
import type { LoginAuditFilters, LoginAuditResponse } from '@/services/securityAuditService';

export function useLoginAudit(filters: LoginAuditFilters) {
  return useQuery<LoginAuditResponse>({
    queryKey: ['login-audit', filters],
    queryFn: () => fetchLoginAudit(filters),
    staleTime: 30_000,
    placeholderData: (previousData) => previousData,
  });
}
