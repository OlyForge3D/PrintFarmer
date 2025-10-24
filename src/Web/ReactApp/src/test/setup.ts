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

// Provide a global mock for the official SignalR package used directly by pages/components
// This ensures code that does `new signalR.HubConnectionBuilder().withUrl(...).build()` works in tests
vi.mock('@microsoft/signalr', () => {
  const mockConnection = {
    start: vi.fn().mockResolvedValue(undefined),
    stop: vi.fn().mockResolvedValue(undefined),
    on: vi.fn(),
    off: vi.fn(),
  };

  const mockBuilder = {
    withUrl: vi.fn().mockReturnThis(),
    withAutomaticReconnect: vi.fn().mockReturnThis(),
    build: vi.fn().mockReturnValue(mockConnection),
  };

  return {
    HubConnectionBuilder: vi.fn().mockImplementation(() => mockBuilder),
    HubConnectionState: {
      Connected: 'Connected',
      Disconnected: 'Disconnected',
    },
  };
});

// Mock harvest SignalR service to avoid connection attempts and warnings in tests
vi.mock('@/services/harvest-signalr', () => ({
  SignalRService: vi.fn().mockImplementation(() => ({
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
  })),
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