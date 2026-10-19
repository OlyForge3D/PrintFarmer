import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { slicerService } from '../slicerService';

// Mock getApiBaseUrl
vi.mock('@/common/utils/apiUrlHelpers', () => ({
  getApiBaseUrl: vi.fn(() => 'http://localhost:5245')
}));

describe('slicerService', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  describe('uploadModel', () => {
    // Regression coverage for issue #2175: the upload queue must surface the server's actual
    // 400 body instead of the generic `xhr.statusText`.
    class MockXhr {
      static instances: MockXhr[] = [];

      status = 0;
      statusText = '';
      responseText = '';
      upload = { addEventListener: vi.fn() };
      private listeners: Record<string, Array<() => void>> = {};

      constructor() {
        MockXhr.instances.push(this);
      }

      addEventListener(event: string, handler: () => void) {
        (this.listeners[event] ??= []).push(handler);
      }

      open = vi.fn();
      setRequestHeader = vi.fn();
      send = vi.fn();

      // Test helper: fire a registered event's handlers.
      trigger(event: string) {
        this.listeners[event]?.forEach((handler) => handler());
      }
    }

    let originalXhr: typeof XMLHttpRequest;

    beforeEach(() => {
      MockXhr.instances = [];
      originalXhr = global.XMLHttpRequest;
      global.XMLHttpRequest = MockXhr as unknown as typeof XMLHttpRequest;
    });

    afterEach(() => {
      global.XMLHttpRequest = originalXhr;
    });

    it('surfaces the server-provided validation reason from a 400 response body, not the generic statusText', async () => {
      const mockFile = new File(['x'], 'malformed.stl', { type: 'application/octet-stream' });
      const validationMessage =
        "Model file 'malformed.stl' failed validation: File is too small to be a valid STL (must be at least 84 bytes) (Parameter 'modelFile')";

      const uploadPromise = slicerService.uploadModel(mockFile);

      const xhr = MockXhr.instances[0];
      xhr.status = 400;
      xhr.statusText = 'Bad Request';
      xhr.responseText = JSON.stringify(validationMessage);
      xhr.trigger('load');

      await expect(uploadPromise).rejects.toThrow(validationMessage);
    });

    it('resolves with the parsed response on success', async () => {
      const mockFile = new File(['x'], 'model.stl', { type: 'application/octet-stream' });
      const mockResult = { id: 'model-123', url: '/api/3d-models/model-123' };

      const uploadPromise = slicerService.uploadModel(mockFile);

      const xhr = MockXhr.instances[0];
      xhr.status = 200;
      xhr.responseText = JSON.stringify(mockResult);
      xhr.trigger('load');

      await expect(uploadPromise).resolves.toEqual(mockResult);
    });
  });
});
