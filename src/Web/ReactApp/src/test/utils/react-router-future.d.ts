declare module 'react-router-dom' {
  // Augment BrowserRouter props in tests so we can pass the experimental
  // `future` opt-in flags without casting to `any` in test helpers.
  // Keep this file under src/test so it's only used for tests.
  export interface BrowserRouterProps {
    future?: {
      v7_preventBasepathDoubleSlash?: boolean;
      v7_useIdInRoutePaths?: boolean;
      v7_startTransition?: boolean;
      v7_relativeSplatPath?: boolean;
      [key: string]: unknown;
    };
  }
}
