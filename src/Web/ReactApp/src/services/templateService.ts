import { apiClient } from '@/services/api';
import {
  PrintProjectTemplateListDto,
  PrintProjectTemplateDetailDto,
  CreatePrintProjectTemplateRequest,
} from '@/types/api';

/**
 * Service for managing print project templates.
 */
export const templateService = {
  /**
   * Get all project templates with optional filtering.
   */
  async getTemplates(options?: {
    category?: string;
    search?: string;
  }): Promise<PrintProjectTemplateListDto[]> {
    const params = new URLSearchParams();
    if (options?.category) params.append('category', options.category);
    if (options?.search) params.append('search', options.search);

    const queryString = params.toString();
    const url = queryString
      ? `/project-templates?${queryString}`
      : '/project-templates';

    const response = await apiClient.request<PrintProjectTemplateListDto[]>({
      method: 'GET',
      url,
    });
    return response.data;
  },

  /**
   * Get all distinct template categories.
   */
  async getCategories(): Promise<string[]> {
    const response = await apiClient.request<string[]>({
      method: 'GET',
      url: '/project-templates/categories',
    });
    return response.data;
  },

  /**
   * Get a single template by ID.
   */
  async getTemplate(templateId: string): Promise<PrintProjectTemplateDetailDto> {
    const response = await apiClient.request<PrintProjectTemplateDetailDto>({
      method: 'GET',
      url: `/project-templates/${templateId}`,
    });
    return response.data;
  },

  /**
   * Create a new project template.
   */
  async createTemplate(
    request: CreatePrintProjectTemplateRequest
  ): Promise<PrintProjectTemplateDetailDto> {
    const response = await apiClient.request<PrintProjectTemplateDetailDto>({
      method: 'POST',
      url: '/project-templates',
      data: request,
    });
    return response.data;
  },

  /**
   * Delete a project template.
   */
  async deleteTemplate(templateId: string): Promise<void> {
    await apiClient.request({
      method: 'DELETE',
      url: `/project-templates/${templateId}`,
    });
  },
};
