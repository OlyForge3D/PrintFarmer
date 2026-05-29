import { apiClient } from '@/services/api';

export interface LoginAuditEntry {
  id: string;
  timestamp: string;
  username: string;
  success: boolean;
  ipAddress: string;
  userAgent: string;
  failureReason: string | null;
}

export interface LoginAuditResponse {
  items: LoginAuditEntry[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface LoginAuditFilters {
  from?: string;
  to?: string;
  username?: string;
  success?: boolean;
  page?: number;
  pageSize?: number;
}

export async function fetchLoginAudit(filters: LoginAuditFilters = {}): Promise<LoginAuditResponse> {
  const params: Record<string, string | number | boolean> = {
    page: filters.page ?? 1,
    pageSize: filters.pageSize ?? 50,
  };

  if (filters.from) {
    const d = new Date(filters.from);
    params.from = isNaN(d.getTime()) ? filters.from : d.toISOString();
  }
  if (filters.to) {
    const d = new Date(filters.to);
    params.to = isNaN(d.getTime()) ? filters.to : d.toISOString();
  }
  if (filters.username) params.username = filters.username;
  if (filters.success !== undefined) params.success = filters.success;

  const response = await apiClient.get<LoginAuditResponse>('/admin/security/login-audit', { params });
  return response.data;
}
