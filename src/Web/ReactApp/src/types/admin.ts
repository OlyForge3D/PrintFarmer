/**
 * Type definitions for admin interface components
 * Consolidated from scattered definitions across the admin feature
 */

/**
 * User information for user management
 */
export interface User {
  id: string;
  username: string;
  email: string;
  firstName?: string;
  lastName?: string;
  isActive: boolean;
  emailConfirmed: boolean;
  lastLogin?: string;
  createdAt: string;
  roles: string[];
  permissions: string[];
}

/**
 * Role definition for role-based access control
 */
export interface Role {
  id: string;
  name: string;
  displayName: string;
  description?: string;
  isSystemRole: boolean;
  isActive: boolean;
}

/**
 * Tag option for tag management
 */
export interface TagOption {
  id: string;
  name: string;
  color?: string;
  description?: string;
  usageCount?: number; // Number of models with this tag
}

/**
 * Tag being edited in the admin interface
 */
export interface EditingTag {
  id?: string;
  name: string;
  color?: string;
  description?: string;
}

/**
 * System log entry for the logs page
 */
export interface SystemLog {
  id: number;
  timestamp: string;
  level: string;
  message: string;
  exception?: string;
  source?: string;
  correlationId?: string;
  metadata?: string;
}

/**
 * Column key type for system logs table
 */
export type LogColumnKey = 'timestamp' | 'level' | 'message' | 'correlationId' | 'source' | 'metadata' | 'exception';
