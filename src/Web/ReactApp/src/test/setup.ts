import { expect, afterEach, vi } from 'vitest';
import { cleanup } from '@testing-library/react';
import * as matchers from '@testing-library/jest-dom/matchers';
import '@testing-library/jest-dom';

// Add ResizeObserver polyfill for three.js and react-three-fiber in tests
// Vitest v4 requires class for constructors
global.ResizeObserver = class MockResizeObserver {
  observe = vi.fn();
  unobserve = vi.fn();
  disconnect = vi.fn();
};

// Add matchMedia polyfill for responsive layout components in tests
Object.defineProperty(window, 'matchMedia', {
  writable: true,
  value: vi.fn().mockImplementation((query: string) => ({
    matches: false,
    media: query,
    onchange: null,
    addListener: vi.fn(), // deprecated
    removeListener: vi.fn(), // deprecated
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    dispatchEvent: vi.fn(),
  })),
});

// Silence noisy Three.js duplicate import warnings in test output.
// Some transitive deps include their own three copy which triggers
// "WARNING: Multiple instances of Three.js being imported." during tests.
// Filter that specific message to keep test logs clean.
const _origConsoleWarn = console.warn.bind(console);
console.warn = (...args: unknown[]) => {
  const first = args[0];
  const msg = typeof first === 'string' ? first : String(first);
  if (msg.includes('Multiple instances of Three.js being imported')) {
    return;
  }
  // Fallback to original console.warn
  _origConsoleWarn(...args as unknown[]);
};

// extends Vitest's expect method with methods from react-testing-library
expect.extend(matchers);

// Mock SignalR service to avoid connection errors in tests
// Vitest v4 requires class for constructors
vi.mock('@/services/signalr', () => ({
  SignalRService: class MockSignalRService {
    connection = null;
    initializeConnection = vi.fn().mockResolvedValue(undefined);
    disconnect = vi.fn().mockResolvedValue(undefined);
    subscribeToDiscoveryProgress = vi.fn();
    subscribeToPrinterUpdates = vi.fn();
    unsubscribeFromDiscoveryProgress = vi.fn();
    unsubscribeFromPrinterUpdates = vi.fn();
    loadSettings = vi.fn().mockResolvedValue({
      baseUrl: 'http://localhost:5245',
      hubPath: '/hubs/printers'
    });
  },
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

// Provide a global mock for the official SignalR package used directly by pages/components
// This ensures code that does `new signalR.HubConnectionBuilder().withUrl(...).build()` works in tests
// Vitest v4 requires class for constructors
vi.mock('@microsoft/signalr', () => {
  const mockConnection = {
    start: vi.fn().mockResolvedValue(undefined),
    stop: vi.fn().mockResolvedValue(undefined),
    on: vi.fn(),
    off: vi.fn(),
    onclose: vi.fn(),
    onreconnecting: vi.fn(),
    onreconnected: vi.fn(),
    state: 'Disconnected'
  };

  return {
    HubConnectionBuilder: class MockHubConnectionBuilder {
      withUrl() { return this; }
      withAutomaticReconnect() { return this; }
      configureLogging() { return this; }
      build() { return mockConnection; }
    },
    HubConnectionState: {
      Connected: 'Connected',
      Disconnected: 'Disconnected',
    },
    HttpTransportType: {
      WebSockets: 1,
      ServerSentEvents: 2,
      LongPolling: 4,
    },
    LogLevel: {
      Trace: 0,
      Debug: 1,
      Information: 2,
      Warning: 3,
      Error: 4,
      Critical: 5,
      None: 6,
    },
  };
});

// Mock harvest SignalR service to avoid connection attempts and warnings in tests
// Vitest v4 requires class for constructors
vi.mock('@/services/harvest-signalr', () => ({
  SignalRService: class MockHarvestSignalRService {
    connect = vi.fn().mockResolvedValue(undefined);
    disconnect = vi.fn().mockResolvedValue(undefined);
    joinHarvestGroup = vi.fn().mockResolvedValue(undefined);
    leaveHarvestGroup = vi.fn().mockResolvedValue(undefined);
    onHarvestFileDiscovered = vi.fn(() => () => {});
    onHarvestFileProgress = vi.fn(() => () => {});
    onHarvestUpdate = vi.fn(() => () => {});
    onJobQueueUpdate = vi.fn(() => () => {});
    onConnectionStateChange = vi.fn(() => () => {});
    dispose = vi.fn();
    loadSettings = vi.fn().mockResolvedValue({ baseUrl: 'http://localhost:5245', hubPath: '/hubs/harvest' });
  },
  signalRService: {
    connect: vi.fn().mockResolvedValue(undefined),
    disconnect: vi.fn().mockResolvedValue(undefined),
    joinHarvestGroup: vi.fn().mockResolvedValue(undefined),
    leaveHarvestGroup: vi.fn().mockResolvedValue(undefined),
    onHarvestFileDiscovered: vi.fn(() => () => {}),
    onHarvestFileProgress: vi.fn(() => () => {}),
    onHarvestUpdate: vi.fn(() => () => {}),
    onJobQueueUpdate: vi.fn(() => () => {}),
    onConnectionStateChange: vi.fn(() => () => {}),
    dispose: vi.fn(),
    loadSettings: vi.fn().mockResolvedValue({ baseUrl: 'http://localhost:5245', hubPath: '/hubs/harvest' }),
  }
}));

// runs a cleanup after each test case (e.g. clearing jsdom)
afterEach(() => {
  cleanup();
});