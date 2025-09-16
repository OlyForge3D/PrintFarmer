import { createContext } from 'react';

export interface SpoolmanState {
  enabled: boolean;
  baseUrl: string | null;
  version: string | null;
  lastEndpoint: string | null;
  lastErrorCategory: string | null;
  lastErrorMessage: string | null;
}

export interface SpoolmanContextValue extends SpoolmanState {
  setEnabled: (v: boolean) => void;
  setBaseUrl: (url: string | null) => void;
  updateProbeSuccess: (info: { version?: string | null; endpoint?: string | null }) => void;
  updateProbeFailure: (info: { errorCategory?: string | null; message?: string | null }) => void;
  clear: () => void;
}

export const SpoolmanContext = createContext<SpoolmanContextValue | undefined>(undefined);