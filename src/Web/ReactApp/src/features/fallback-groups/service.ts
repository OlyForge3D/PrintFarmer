/**
 * Filament fallback groups API service (issue #718 / F6 backend #711).
 *
 * Wraps the generic `apiClient` HTTP helpers and decodes wire payloads into
 * strict domain types. All routes live under
 * `/api/printers/{printerId}/fallback-groups`.
 *
 * The backend surfaces validation failures as RFC 7807 ProblemDetails with a
 * 400 status. That contract is honored by the axios error interceptor which
 * projects the response into `ApiError { statusCode, message, details }`.
 * Callers of these methods can display `error.message` and, when present,
 * `error.details` for the human-readable reason without any further parsing.
 */

import { apiClient } from "@/services/api";
import {
  decodeFilamentFallbackGroup,
  decodeFilamentFallbackGroups,
  type CreateFilamentFallbackGroupRequest,
  type FilamentFallbackGroup,
  type UpdateFilamentFallbackGroupRequest,
} from "./types";

const groupsRoot = (printerId: string) =>
  `/printers/${encodeURIComponent(printerId)}/fallback-groups`;

const groupResource = (printerId: string, groupId: string) =>
  `${groupsRoot(printerId)}/${encodeURIComponent(groupId)}`;

export const fallbackGroupsService = {
  async list(printerId: string, signal?: AbortSignal): Promise<FilamentFallbackGroup[]> {
    const response = await apiClient.get<unknown>(groupsRoot(printerId), { signal });
    return decodeFilamentFallbackGroups(response.data);
  },

  async get(
    printerId: string,
    groupId: string,
    signal?: AbortSignal,
  ): Promise<FilamentFallbackGroup> {
    const response = await apiClient.get<unknown>(groupResource(printerId, groupId), { signal });
    return decodeFilamentFallbackGroup(response.data);
  },

  async create(
    printerId: string,
    request: CreateFilamentFallbackGroupRequest,
  ): Promise<FilamentFallbackGroup> {
    const response = await apiClient.post<unknown>(groupsRoot(printerId), request);
    return decodeFilamentFallbackGroup(response.data);
  },

  async update(
    printerId: string,
    groupId: string,
    request: UpdateFilamentFallbackGroupRequest,
  ): Promise<FilamentFallbackGroup> {
    const response = await apiClient.put<unknown>(groupResource(printerId, groupId), request);
    return decodeFilamentFallbackGroup(response.data);
  },

  async remove(printerId: string, groupId: string): Promise<void> {
    await apiClient.delete<void>(groupResource(printerId, groupId));
  },
};
