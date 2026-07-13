/**
 * React Query hooks for the printed-parts inventory feature (issue #721 / F9).
 *
 * All mutations invalidate related caches so the UI does not read stale
 * on-hand or reorder state after a write. Stock changes are always routed
 * through the adjustment ledger — the mutation surface deliberately omits
 * a "set on-hand" helper.
 */

import { useCallback } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { partsInventoryService } from '@/services/partsInventoryService';
import type {
  AdjustPartInventoryRequest,
  BinDto,
  CreateBinRequest,
  CreatePartInventoryRequest,
  CreatePartOutputMappingRequest,
  PartAdjustmentDto,
  PartInventoryDto,
  PartOutputMappingDto,
  RegisterBinBarcodeRequest,
  ReorderCandidateDto,
  UpdateBinRequest,
  UpdatePartInventoryRequest,
} from '@/types/partsInventory';

const STALE_LIST_MS = 30_000;
const STALE_DETAIL_MS = 30_000;
const STALE_LEDGER_MS = 15_000;

export const partsInventoryKeys = {
  all: ['parts-inventory'] as const,
  parts: () => [...partsInventoryKeys.all, 'parts'] as const,
  partsList: (includeInactive: boolean) =>
    [...partsInventoryKeys.parts(), 'list', { includeInactive }] as const,
  part: (sku: string) => [...partsInventoryKeys.parts(), 'detail', sku] as const,
  adjustments: (sku: string, limit: number) =>
    [...partsInventoryKeys.parts(), 'adjustments', sku, limit] as const,
  reorder: () => [...partsInventoryKeys.all, 'reorder'] as const,
  mappings: () => [...partsInventoryKeys.all, 'mappings'] as const,
  mappingList: (sku: string | undefined) =>
    [...partsInventoryKeys.mappings(), 'list', sku ?? 'all'] as const,
  bins: () => [...partsInventoryKeys.all, 'bins'] as const,
  binList: (includeInactive: boolean) =>
    [...partsInventoryKeys.bins(), 'list', { includeInactive }] as const,
  bin: (code: string) => [...partsInventoryKeys.bins(), 'detail', code] as const,
};

// ── Queries ──────────────────────────────────────────────────────────────

export function useParts(options?: { includeInactive?: boolean }) {
  const includeInactive = options?.includeInactive ?? false;
  return useQuery<PartInventoryDto[]>({
    queryKey: partsInventoryKeys.partsList(includeInactive),
    queryFn: () => partsInventoryService.listParts({ includeInactive }),
    staleTime: STALE_LIST_MS,
  });
}

export function usePart(sku: string | undefined) {
  return useQuery<PartInventoryDto>({
    queryKey: partsInventoryKeys.part(sku ?? ''),
    queryFn: () => partsInventoryService.getPart(sku as string),
    enabled: Boolean(sku),
    staleTime: STALE_DETAIL_MS,
  });
}

export function usePartAdjustments(sku: string | undefined, limit = 100) {
  return useQuery<PartAdjustmentDto[]>({
    queryKey: partsInventoryKeys.adjustments(sku ?? '', limit),
    queryFn: () => partsInventoryService.listAdjustments(sku as string, limit),
    enabled: Boolean(sku),
    staleTime: STALE_LEDGER_MS,
  });
}

export function useReorderCandidates() {
  return useQuery<ReorderCandidateDto[]>({
    queryKey: partsInventoryKeys.reorder(),
    queryFn: () => partsInventoryService.listReorderCandidates(),
    staleTime: STALE_LIST_MS,
  });
}

export function useMappings(sku?: string) {
  return useQuery<PartOutputMappingDto[]>({
    queryKey: partsInventoryKeys.mappingList(sku),
    queryFn: () => partsInventoryService.listMappings(sku),
    staleTime: STALE_LIST_MS,
  });
}

export function useBins(options?: { includeInactive?: boolean }) {
  const includeInactive = options?.includeInactive ?? false;
  return useQuery<BinDto[]>({
    queryKey: partsInventoryKeys.binList(includeInactive),
    queryFn: () => partsInventoryService.listBins({ includeInactive }),
    staleTime: STALE_LIST_MS,
  });
}

// ── Mutations ────────────────────────────────────────────────────────────

function useInvalidateParts() {
  const queryClient = useQueryClient();
  return useCallback(() => {
    queryClient.invalidateQueries({ queryKey: partsInventoryKeys.parts() });
    queryClient.invalidateQueries({ queryKey: partsInventoryKeys.reorder() });
  }, [queryClient]);
}

function useInvalidateAll() {
  const queryClient = useQueryClient();
  return useCallback(() => {
    queryClient.invalidateQueries({ queryKey: partsInventoryKeys.all });
  }, [queryClient]);
}

export function useCreatePart() {
  const invalidate = useInvalidateParts();
  return useMutation({
    mutationFn: (request: CreatePartInventoryRequest) => partsInventoryService.createPart(request),
    onSuccess: () => invalidate(),
  });
}

export function useUpdatePart() {
  const invalidate = useInvalidateParts();
  return useMutation({
    mutationFn: ({ sku, request }: { sku: string; request: UpdatePartInventoryRequest }) =>
      partsInventoryService.updatePart(sku, request),
    onSuccess: () => invalidate(),
  });
}

export function useDeletePart() {
  const invalidate = useInvalidateParts();
  return useMutation({
    mutationFn: (sku: string) => partsInventoryService.deletePart(sku),
    onSuccess: () => invalidate(),
  });
}

export function useAdjustPartStock() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ sku, request }: { sku: string; request: AdjustPartInventoryRequest }) =>
      partsInventoryService.adjustStock(sku, request),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({ queryKey: partsInventoryKeys.parts() });
      queryClient.invalidateQueries({ queryKey: partsInventoryKeys.reorder() });
      queryClient.invalidateQueries({
        queryKey: [...partsInventoryKeys.parts(), 'adjustments', variables.sku],
      });
    },
  });
}

export function useCreateMapping() {
  const invalidate = useInvalidateAll();
  return useMutation({
    mutationFn: (request: CreatePartOutputMappingRequest) =>
      partsInventoryService.createMapping(request),
    onSuccess: () => invalidate(),
  });
}

export function useDeleteMapping() {
  const invalidate = useInvalidateAll();
  return useMutation({
    mutationFn: (id: string) => partsInventoryService.deleteMapping(id),
    onSuccess: () => invalidate(),
  });
}

export function useCreateBin() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (request: CreateBinRequest) => partsInventoryService.createBin(request),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: partsInventoryKeys.bins() }),
  });
}

export function useUpdateBin() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ code, request }: { code: string; request: UpdateBinRequest }) =>
      partsInventoryService.updateBin(code, request),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: partsInventoryKeys.bins() }),
  });
}

export function useDeleteBin() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (code: string) => partsInventoryService.deleteBin(code),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: partsInventoryKeys.bins() }),
  });
}

export function useRegisterBinBarcode() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (request: RegisterBinBarcodeRequest) =>
      partsInventoryService.registerBinBarcode(request),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: partsInventoryKeys.bins() }),
  });
}
