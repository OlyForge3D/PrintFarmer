import { apiClient } from '@/services/api';
import type {
  PrintProjectListDto,
  PrintProjectDetailDto,
  PrintProjectProgressDto,
  CreatePrintProjectRequest,
  UpdatePrintProjectRequest,
  AddFileToProjectRequest,
  UpdateProjectFileRequest,
  PrintProjectStatus,
  PrintProjectFileDto,
  QueueProjectRequest,
  QueueProjectResultDto,
} from '@/types/api';

/**
 * Service for managing print projects.
 * All API calls go through apiClient for consistent auth and error handling.
 */
export const projectService = {
  /**
   * Get all projects with optional filters.
   */
  async getProjects(params?: {
    status?: PrintProjectStatus;
    search?: string;
  }): Promise<PrintProjectListDto[]> {
    const queryParams = new URLSearchParams();
    if (params?.status) queryParams.append('status', params.status);
    if (params?.search) queryParams.append('search', params.search);
    
    const query = queryParams.toString();
    const url = query ? `/projects?${query}` : '/projects';
    const response = await apiClient.genericGet(url);
    return response as unknown as PrintProjectListDto[];
  },

  /**
   * Get a single project by ID with all file details.
   */
  async getProject(id: string): Promise<PrintProjectDetailDto> {
    const response = await apiClient.genericGet(`/projects/${id}`);
    return response as unknown as PrintProjectDetailDto;
  },

  /**
   * Create a new project.
   */
  async createProject(request: CreatePrintProjectRequest): Promise<PrintProjectDetailDto> {
    const response = await apiClient.genericPost('/projects', request as unknown as Record<string, unknown>);
    return response as unknown as PrintProjectDetailDto;
  },

  /**
   * Update an existing project.
   */
  async updateProject(id: string, request: UpdatePrintProjectRequest): Promise<PrintProjectDetailDto> {
    const response = await apiClient.genericPut(`/projects/${id}`, request as unknown as Record<string, unknown>);
    return response as unknown as PrintProjectDetailDto;
  },

  /**
   * Delete a project.
   */
  async deleteProject(id: string): Promise<void> {
    await apiClient.genericDelete(`/projects/${id}`);
  },

  /**
   * Add a file to a project.
   */
  async addFileToProject(projectId: string, request: AddFileToProjectRequest): Promise<PrintProjectFileDto> {
    const response = await apiClient.genericPost(
      `/projects/${projectId}/files`,
      request as unknown as Record<string, unknown>
    );
    return response as unknown as PrintProjectFileDto;
  },

  /**
   * Add multiple files to a project at once.
   */
  async addFilesToProject(projectId: string, requests: AddFileToProjectRequest[]): Promise<PrintProjectFileDto[]> {
    const results: PrintProjectFileDto[] = [];
    for (const request of requests) {
      const file = await projectService.addFileToProject(projectId, request);
      results.push(file);
    }
    return results;
  },

  /**
   * Remove a file from a project.
   */
  async removeFileFromProject(projectId: string, fileId: string): Promise<void> {
    await apiClient.genericDelete(`/projects/${projectId}/files/${fileId}`);
  },

  /**
   * Update a file within a project.
   */
  async updateProjectFile(
    projectId: string,
    fileId: string,
    request: UpdateProjectFileRequest
  ): Promise<PrintProjectFileDto> {
    const response = await apiClient.genericPut(
      `/projects/${projectId}/files/${fileId}`,
      request as unknown as Record<string, unknown>
    );
    return response as unknown as PrintProjectFileDto;
  },

  /**
   * Mark a file as printed (increments printed count).
   */
  async markFilePrinted(projectId: string, fileId: string, printJobId?: string): Promise<PrintProjectFileDto> {
    const response = await apiClient.genericPost(
      `/projects/${projectId}/files/${fileId}/mark-printed`,
      printJobId ? { printJobId } : {}
    );
    return response as unknown as PrintProjectFileDto;
  },

  /**
   * Get project progress summary.
   */
  async getProjectProgress(projectId: string): Promise<PrintProjectProgressDto> {
    const response = await apiClient.genericGet(`/projects/${projectId}/progress`);
    return response as unknown as PrintProjectProgressDto;
  },

  /**
   * Queue all pending files from a project to the job queue.
   * Files are automatically ordered by material type and color to minimize filament changes.
   */
  async queueProject(projectId: string, request?: QueueProjectRequest): Promise<QueueProjectResultDto> {
    const response = await apiClient.genericPost(
      `/projects/${projectId}/queue`,
      (request ?? {}) as unknown as Record<string, unknown>
    );
    return response as unknown as QueueProjectResultDto;
  },
};
