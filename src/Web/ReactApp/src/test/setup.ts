import { expect, afterEach, vi } from 'vitest';
import { cleanup } from '@testing-library/react';
import * as matchers from '@testing-library/jest-dom/matchers';
import '@testing-library/jest-dom';

// extends Vitest's expect method with methods from react-testing-library
expect.extend(matchers);

// Mock SignalR service to avoid connection errors in tests
vi.mock('@/services/signalr', () => ({
  SignalRService: vi.fn().mockImplementation(() => ({
    connection: null,
    initializeConnection: vi.fn().mockResolvedValue(undefined),
    disconnect: vi.fn().mockResolvedValue(undefined),
    subscribeToDiscoveryProgress: vi.fn(),
    subscribeToPrinterUpdates: vi.fn(),
    unsubscribeFromDiscoveryProgress: vi.fn(),
    unsubscribeFromPrinterUpdates: vi.fn(),
    loadSettings: vi.fn().mockResolvedValue({
      baseUrl: 'http://localhost:5245',
      hubPath: '/hubs/printers'
    }),
  })),
  signalRService: {
    connection: null,
    initializeConnection: vi.fn().mockResolvedValue(undefined),
    disconnect: vi.fn().mockResolvedValue(undefined),
    subscribeToDiscoveryProgress: vi.fn(),
    subscribeToPrinterUpdates: vi.fn(),
    unsubscribeFromDiscoveryProgress: vi.fn(),
    unsubscribeFromPrinterUpdates: vi.fn(),
    loadSettings: vi.fn().mockResolvedValue({
      baseUrl: 'http://localhost:5245',
      hubPath: '/hubs/printers'
    }),
  }
}));

// runs a cleanup after each test case (e.g. clearing jsdom)
afterEach(() => {
  cleanup();
});