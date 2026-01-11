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
  role: string;
  isActive: boolean;
  createdAt: string;
  lastLoginAt?: string;
}

/**
 * Role definition for role-based access control
 */
export interface Role {
  id: string;
  name: string;
  description?: string;
  permissions: string[];
}

/**
 * Tag option for tag management
 */
export interface TagOption {
  id: string;
  name: string;
  color?: string;
  description?: string;
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
  id: string;
  timestamp: string;
  level: 'debug' | 'info' | 'warning' | 'error' | 'critical';
  message: string;
  correlationId?: string;
  source?: string;
  metadata?: Record<string, any>;
  exception?: string;
}

/**
 * Column key type for system logs table
 */
export type LogColumnKey = 'timestamp' | 'level' | 'message' | 'correlationId' | 'source' | 'metadata' | 'exception';
