// Slicer service interfaces and types
import { apiClient } from './api';
import { getApiBaseUrl } from '@/common/utils/apiUrlHelpers';
import { parseXhrErrorMessage } from '@/common/utils/apiErrors';

class SlicerService {
  // Model management - uses XHR for progress tracking
  async uploadModel(
    file: File,
    onProgress?: (progress: number) => void
  ): Promise<{ id: string; url: string }> {
    const formData = new FormData();
    formData.append('modelFile', file);

    const baseUrl = getApiBaseUrl();
    const uploadUrl = `${baseUrl}/3d-models/upload`;

    // XMLHttpRequest is needed for progress tracking with fetch
    return new Promise((resolve, reject) => {
      const xhr = new XMLHttpRequest();

      if (onProgress) {
        xhr.upload.addEventListener('progress', (e) => {
          if (e.lengthComputable) {
            const progress = Math.round((e.loaded / e.total) * 100);
            onProgress(progress);
          }
        });
      }

      xhr.addEventListener('load', () => {
        if (xhr.status >= 200 && xhr.status < 300) {
          try {
            const response = JSON.parse(xhr.responseText);
            resolve(response);
          } catch {
            reject(new Error('Failed to parse upload response'));
          }
        } else {
          // Surface the server's actual validation reason instead of the generic
          // statusText (e.g. "Bad Request") so users know why a file was rejected (#2175).
          const message = parseXhrErrorMessage(
            xhr.responseText,
            `Model upload failed: ${xhr.statusText}`
          );
          reject(new Error(message));
        }
      });

      xhr.addEventListener('error', () => {
        reject(new Error('Network error during upload'));
      });

      xhr.addEventListener('abort', () => {
        reject(new Error('Upload cancelled'));
      });

      xhr.open('POST', uploadUrl);
      xhr.setRequestHeader('Authorization', `Bearer ${localStorage.getItem('auth-token')}`);
      xhr.send(formData);
    });
  }

  /**
   * List all slicer engines currently registered in the plugin registry,
   * including all installed versions per engine. Fed by the API endpoint
   * added for issue #578 dual-engine support.
   */
  async listEngines(): Promise<SlicerEngineInfo[]> {
    const response = await apiClient.get<SlicerEngineInfo[]>('/slicers/engines');
    return response.data ?? [];
  }
}

/**
 * A slicer engine surface with all installed versions (issue #578).
 * Emitted by GET /api/slicers/engines.
 */
export interface SlicerEngineInfo {
  /** Canonical engine name (e.g. "OrcaSlicer", "PrusaSlicer"). */
  engine: string;
  /** All installed versions, newest first (System.Version sort). */
  versions: string[];
  /**
   * Per-version availability. `available` is `true` only when at least one
   * Online SlicerService currently advertises that (engine, version) pair.
   * When no SlicerService rows exist yet (fresh install), the backend marks
   * every entry available so the version selector is still usable.
   * The UI MUST use this to disable unavailable versions in the selector —
   * a job pinned to an unavailable version will otherwise hang in the queue.
   */
  versionEntries: SlicerEngineVersionEntry[];
  /**
   * The newest AVAILABLE version, or `null` when nothing is available right now.
   * The backend computes this as `versionEntries.FirstOrDefault(v => v.available)`
   * (SlicersController.ListEnginesAsync), so it NEVER names an unavailable
   * version — an all-unavailable engine yields `null`, as does the legacy
   * no-service-rows case. NewSliceJobPage's Latest-mode submit guard depends on
   * exactly that: it reads a null `latest` as "no pin target exists".
   * Prefer this over `versions[0]` when defaulting an unpinned submission.
   */
  latest: string | null;
}

export interface SlicerEngineVersionEntry {
  version: string;
  available: boolean;
}

export const slicerService = new SlicerService();