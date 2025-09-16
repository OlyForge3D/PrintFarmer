import React, { useState, useCallback } from 'react';
import { SpoolmanState, SpoolmanContextValue, SpoolmanContext } from './SpoolmanTypes';

const defaultState: SpoolmanState = {
  enabled: false,
  baseUrl: null,
  version: null,
  lastEndpoint: null,
  lastErrorCategory: null,
  lastErrorMessage: null
};

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
