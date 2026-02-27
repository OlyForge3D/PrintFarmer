import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '@/services/api';
import type { WebhookSubscription, CreateWebhookDto, UpdateWebhookDto, WebhookDelivery } from '@/types/api';

const KEYS = {
  all: ['webhooks'] as const,
  list: () => [...KEYS.all, 'list'] as const,
  detail: (id: string) => [...KEYS.all, 'detail', id] as const,
  deliveries: (id: string) => [...KEYS.all, 'deliveries', id] as const,
  eventTypes: () => [...KEYS.all, 'event-types'] as const,
};

export function useWebhooks() {
  return useQuery<WebhookSubscription[]>({
    queryKey: KEYS.list(),
    queryFn: async () => {
      const res = await apiClient.get('/webhooks');
      return res.data;
    },
  });
}

export function useWebhookEventTypes() {
  return useQuery<string[]>({
    queryKey: KEYS.eventTypes(),
    queryFn: async () => {
      const res = await apiClient.get('/webhooks/event-types');
      return res.data;
    },
    staleTime: Infinity,
  });
}

export function useWebhookDeliveries(id: string) {
  return useQuery<WebhookDelivery[]>({
    queryKey: KEYS.deliveries(id),
    queryFn: async () => {
      const res = await apiClient.get(`/webhooks/${id}/deliveries`);
      return res.data;
    },
    enabled: !!id,
  });
}

export function useCreateWebhook() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (dto: CreateWebhookDto) => {
      const res = await apiClient.post('/webhooks', dto);
      return res.data as WebhookSubscription;
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: KEYS.list() }),
  });
}

export function useUpdateWebhook() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async ({ id, dto }: { id: string; dto: UpdateWebhookDto }) => {
      const res = await apiClient.put(`/webhooks/${id}`, dto);
      return res.data as WebhookSubscription;
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: KEYS.list() }),
  });
}

export function useDeleteWebhook() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) => {
      await apiClient.delete(`/webhooks/${id}`);
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: KEYS.list() }),
  });
}

export function useTestWebhook() {
  return useMutation({
    mutationFn: async (id: string) => {
      const res = await apiClient.post(`/webhooks/${id}/test`);
      return res.data;
    },
  });
}
