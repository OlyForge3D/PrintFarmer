import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { apiClient } from '@/services/api';
import type { ApiError } from '@/types/api';
import type {
  ModelCollection,
  ModelCollectionMembership,
  CreateModelCollectionRequest,
  UpdateModelCollectionRequest,
} from '@/types/models';

/**
 * Query keys for the model collections feature (#843/#846). Kept feature-local since
 * collections are only consumed within features/models3d today.
 */
export const collectionQueryKeys = {
  all: ['model-collections'] as const,
  list: () => [...collectionQueryKeys.all, 'list'] as const,
  detail: (id: string) => [...collectionQueryKeys.all, 'detail', id] as const,
  members: (id: string) => [...collectionQueryKeys.all, 'members', id] as const,
};

/** Lists all collections visible to the current user (owned + shared). */
export function useModelCollections() {
  return useQuery<ModelCollection[]>({
    queryKey: collectionQueryKeys.list(),
    queryFn: () => apiClient.getModelCollections(),
    staleTime: 30 * 1000,
  });
}

/** Fetches a single collection's metadata. */
export function useModelCollection(id: string | null) {
  return useQuery<ModelCollection>({
    queryKey: collectionQueryKeys.detail(id ?? ''),
    queryFn: () => apiClient.getModelCollection(id as string),
    enabled: !!id,
    staleTime: 30 * 1000,
  });
}

/** Lists the model memberships of a collection. Disabled until a collection is selected. */
export function useModelCollectionMembers(collectionId: string | null) {
  return useQuery<ModelCollectionMembership[]>({
    queryKey: collectionQueryKeys.members(collectionId ?? ''),
    queryFn: () => apiClient.listModelCollectionMembers(collectionId as string),
    enabled: !!collectionId,
    staleTime: 15 * 1000,
  });
}

function invalidateCollectionLists(queryClient: ReturnType<typeof useQueryClient>) {
  queryClient.invalidateQueries({ queryKey: collectionQueryKeys.list() });
}

export function useCreateModelCollection() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (dto: CreateModelCollectionRequest) => apiClient.createModelCollection(dto),
    onSuccess: () => {
      invalidateCollectionLists(queryClient);
      toast.success('Collection created');
    },
    onError: (error: ApiError) => {
      toast.error(`Failed to create collection: ${error.message}`);
    },
  });
}

export function useUpdateModelCollection() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, dto }: { id: string; dto: UpdateModelCollectionRequest }) =>
      apiClient.updateModelCollection(id, dto),
    onSuccess: (_data, variables) => {
      invalidateCollectionLists(queryClient);
      queryClient.invalidateQueries({ queryKey: collectionQueryKeys.detail(variables.id) });
      toast.success('Collection updated');
    },
    onError: (error: ApiError) => {
      toast.error(`Failed to update collection: ${error.message}`);
    },
  });
}

export function useDeleteModelCollection() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => apiClient.deleteModelCollection(id),
    onSuccess: () => {
      invalidateCollectionLists(queryClient);
      toast.success('Collection deleted');
    },
    onError: (error: ApiError) => {
      toast.error(`Failed to delete collection: ${error.message}`);
    },
  });
}

export function useShareModelCollection() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => apiClient.shareModelCollection(id),
    onSuccess: (_data, id) => {
      invalidateCollectionLists(queryClient);
      queryClient.invalidateQueries({ queryKey: collectionQueryKeys.detail(id) });
      toast.success('Collection shared with all users');
    },
    onError: (error: ApiError) => {
      toast.error(`Failed to share collection: ${error.message}`);
    },
  });
}

export function useUnshareModelCollection() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => apiClient.unshareModelCollection(id),
    onSuccess: (_data, id) => {
      invalidateCollectionLists(queryClient);
      queryClient.invalidateQueries({ queryKey: collectionQueryKeys.detail(id) });
      toast.success('Collection is now private');
    },
    onError: (error: ApiError) => {
      toast.error(`Failed to unshare collection: ${error.message}`);
    },
  });
}

/**
 * Adds one or more models to a collection. There is no batch-membership REST endpoint
 * outside the desktop offline-sync protocol, so this issues parallel single-item requests
 * behind one mutation/one loading state/one toast, per #846's "efficient multi-model
 * membership actions" requirement.
 *
 * Uses `Promise.allSettled` (not `Promise.all`) so a single failed request doesn't abort the
 * others mid-flight - the mutation always waits for every request to finish, invalidates the
 * membership/collection caches so the UI reflects whatever partially succeeded, and reports
 * a specific error (with the failure count) rather than silently losing the failures.
 */
export function useAddModelsToCollection() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ collectionId, modelIds }: { collectionId: string; modelIds: string[] }) => {
      const results = await Promise.allSettled(
        modelIds.map((modelId) => apiClient.addModelCollectionMember(collectionId, modelId))
      );
      const failures = results.filter((result): result is PromiseRejectedResult => result.status === 'rejected');
      return { succeededCount: results.length - failures.length, failures };
    },
    onSettled: (_data, _error, variables) => {
      invalidateCollectionLists(queryClient);
      queryClient.invalidateQueries({ queryKey: collectionQueryKeys.members(variables.collectionId) });
      queryClient.invalidateQueries({ queryKey: collectionQueryKeys.detail(variables.collectionId) });
    },
    onSuccess: (result, variables) => {
      if (result.failures.length === 0) {
        const count = variables.modelIds.length;
        toast.success(`Added ${count} model${count === 1 ? '' : 's'} to collection`);
      } else if (result.succeededCount > 0) {
        toast.error(
          `Added ${result.succeededCount} model${result.succeededCount === 1 ? '' : 's'}, but ${result.failures.length} failed to add. Please retry the failed ones.`
        );
      } else {
        toast.error('Failed to add models to collection.');
      }
    },
    onError: (error: ApiError) => {
      toast.error(`Failed to add models to collection: ${error.message}`);
    },
  });
}

/** Removes one or more models from a collection. See {@link useAddModelsToCollection}. */
export function useRemoveModelsFromCollection() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async ({ collectionId, modelIds }: { collectionId: string; modelIds: string[] }) => {
      const results = await Promise.allSettled(
        modelIds.map((modelId) => apiClient.removeModelCollectionMember(collectionId, modelId))
      );
      const failures = results.filter((result): result is PromiseRejectedResult => result.status === 'rejected');
      return { succeededCount: results.length - failures.length, failures };
    },
    onSettled: (_data, _error, variables) => {
      invalidateCollectionLists(queryClient);
      queryClient.invalidateQueries({ queryKey: collectionQueryKeys.members(variables.collectionId) });
      queryClient.invalidateQueries({ queryKey: collectionQueryKeys.detail(variables.collectionId) });
    },
    onSuccess: (result, variables) => {
      if (result.failures.length === 0) {
        const count = variables.modelIds.length;
        toast.success(`Removed ${count} model${count === 1 ? '' : 's'} from collection`);
      } else if (result.succeededCount > 0) {
        toast.error(
          `Removed ${result.succeededCount} model${result.succeededCount === 1 ? '' : 's'}, but ${result.failures.length} failed to remove. Please retry the failed ones.`
        );
      } else {
        toast.error('Failed to remove models from collection.');
      }
    },
    onError: (error: ApiError) => {
      toast.error(`Failed to remove models from collection: ${error.message}`);
    },
  });
}
