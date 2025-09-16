import React, { createContext, useState, useCallback } from 'react';

export interface SpoolmanState {
  enabled: boolean;
  baseUrl: string | null;
  version: string | null;
  lastEndpoint: string | null;
  lastErrorCategory: string | null;
  lastErrorMessage: string | null;
}

interface SpoolmanContextValue extends SpoolmanState {
  setEnabled: (v: boolean) => void;
  setBaseUrl: (url: string | null) => void;
  updateProbeSuccess: (info: { version?: string | null; endpoint?: string | null }) => void;
  updateProbeFailure: (info: { errorCategory?: string | null; message?: string | null }) => void;
  clear: () => void;
}

const defaultState: SpoolmanState = {
  enabled: false,
  baseUrl: null,
  version: null,
  lastEndpoint: null,
  lastErrorCategory: null,
  lastErrorMessage: null
};

export const SpoolmanContext = createContext<SpoolmanContextValue | undefined>(undefined);

export const SpoolmanProvider: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const [state, setState] = useState<SpoolmanState>(defaultState);

  const setEnabled = useCallback((v: boolean) => setState(s => ({ ...s, enabled: v })), []);
  const setBaseUrl = useCallback((url: string | null) => setState(s => ({ ...s, baseUrl: url })), []);

  const updateProbeSuccess = useCallback((info: { version?: string | null; endpoint?: string | null }) => {
    setState(s => ({
      ...s,
      version: info.version ?? s.version,
      lastEndpoint: info.endpoint ?? s.lastEndpoint,
      lastErrorCategory: null,
      lastErrorMessage: null
    }));
  }, []);

  const updateProbeFailure = useCallback((info: { errorCategory?: string | null; message?: string | null }) => {
    setState(s => ({
      ...s,
      lastErrorCategory: info.errorCategory ?? s.lastErrorCategory,
      lastErrorMessage: info.message ?? s.lastErrorMessage
    }));
  }, []);

  const clear = useCallback(() => setState(defaultState), []);

  const value: SpoolmanContextValue = {
    ...state,
    setEnabled,
    setBaseUrl,
    updateProbeSuccess,
    updateProbeFailure,
    clear
  };

  return <SpoolmanContext.Provider value={value}>{children}</SpoolmanContext.Provider>;
};
