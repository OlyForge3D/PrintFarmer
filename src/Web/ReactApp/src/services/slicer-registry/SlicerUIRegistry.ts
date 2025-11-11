/**
 * Slicer UI Registry
 *
 * Manages registration and retrieval of slicer-specific UI components and services.
 * Each slicer library exports UI components, services, and types via a SlicerUIExports object,
 * which is registered here by slicer name and version.
 *
 * This allows the core React app to remain slicer-agnostic while dynamically loading
 * slicer-specific UI as needed.
 */

import React from 'react';

// eslint-disable-next-line @typescript-eslint/no-explicit-any
type ComponentType = React.ComponentType<any>;
// eslint-disable-next-line @typescript-eslint/no-explicit-any
type ServiceType = any;
// eslint-disable-next-line @typescript-eslint/no-explicit-any
type ManifestType = Record<string, any>;
// eslint-disable-next-line @typescript-eslint/no-explicit-any
type HookType = (...args: any[]) => any;

export interface SlicerUIExports {
  /** Slicer name (e.g., "OrcaSlicer", "PrusaSlicer") */
  slicerName: string;
  /** Slicer version (e.g., "2.3.1") */
  slicerVersion: string;

  /** React component for bundle import (OrcaSlicer specific) */
  ImportComponent?: ComponentType;
  /** React component for slicer-specific settings */
  SettingsComponent?: ComponentType;
  /** React component for profile editor */
  ProfileEditorComponent?: ComponentType;

  /** Slicer-specific profile service */
  profilesService?: ServiceType;
  /** Slicer-specific asset service */
  assetService?: ServiceType;

  /** TypeScript type definitions */
  types?: ManifestType;

  /** Asset manifest (bed models, textures, cover images) */
  assetManifest?: ManifestType;

  /** Custom React hooks */
  hooks?: Record<string, HookType>;
}

export interface ISlicerUIRegistry {
  /**
   * Register slicer UI exports for a specific slicer and version.
   */
  registerUI(slicerName: string, slicerVersion: string, ui: SlicerUIExports): void;

  /**
   * Get slicer UI exports by name and version.
   */
  getUI(slicerName: string, slicerVersion?: string): SlicerUIExports | null;

  /**
   * Get a specific UI component for a slicer.
   */
  getComponent(slicerName: string, componentName: keyof SlicerUIExports): ComponentType | null;

  /**
   * Get a service for a slicer.
   */
  getService(slicerName: string, serviceName: string): ServiceType;

  /**
   * Get asset manifest for a slicer.
   */
  getAssetManifest(slicerName: string): ManifestType | null;

  /**
   * List all registered slicers.
   */
  listRegistered(): Array<{ name: string; version: string }>;
}

/**
 * Implementation of ISlicerUIRegistry
 */
export class SlicerUIRegistry implements ISlicerUIRegistry {
  private registry = new Map<string, SlicerUIExports>();

  registerUI(slicerName: string, slicerVersion: string, ui: SlicerUIExports): void {
    const key = this.getKey(slicerName, slicerVersion);
    this.registry.set(key, ui);
    console.debug(`[SlicerUIRegistry] Registered UI for ${slicerName} v${slicerVersion}`);
  }

  getUI(slicerName: string, slicerVersion?: string): SlicerUIExports | null {
    // If version specified, look for exact match
    if (slicerVersion) {
      const key = this.getKey(slicerName, slicerVersion);
      return this.registry.get(key) ?? null;
    }

    // Otherwise, find latest version of this slicer
    let latest: SlicerUIExports | null = null;
    let latestVersion = '0.0.0';

    for (const [key, ui] of this.registry) {
      if (key.startsWith(`${slicerName}:`)) {
        if (this.compareVersions(ui.slicerVersion, latestVersion) > 0) {
          latest = ui;
          latestVersion = ui.slicerVersion;
        }
      }
    }

    return latest;
  }

  getComponent(slicerName: string, componentName: keyof SlicerUIExports): ComponentType | null {
    const ui = this.getUI(slicerName);
    if (!ui) return null;

    const component = ui[componentName];
    return typeof component === 'function' ? (component as ComponentType) : null;
  }

  getService(slicerName: string, serviceName: string): ServiceType {
    const ui = this.getUI(slicerName);
    if (!ui) return null;

    // Services are typically in ui.profilesService, ui.assetService, etc.
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    return (ui as any)[serviceName] ?? null;
  }

  getAssetManifest(slicerName: string): ManifestType | null {
    const ui = this.getUI(slicerName);
    return ui?.assetManifest ?? null;
  }

  listRegistered(): Array<{ name: string; version: string }> {
    return Array.from(this.registry.values()).map(ui => ({
      name: ui.slicerName,
      version: ui.slicerVersion,
    }));
  }

  private getKey(slicerName: string, version: string): string {
    return `${slicerName}:${version}`;
  }

  private compareVersions(v1: string, v2: string): number {
    const parts1 = v1.split('.').map(Number);
    const parts2 = v2.split('.').map(Number);

    for (let i = 0; i < Math.max(parts1.length, parts2.length); i++) {
      const p1 = parts1[i] ?? 0;
      const p2 = parts2[i] ?? 0;
      if (p1 !== p2) return p1 - p2;
    }

    return 0;
  }
}
