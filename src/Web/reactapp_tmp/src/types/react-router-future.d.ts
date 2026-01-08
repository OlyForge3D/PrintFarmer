import 'react-router-dom';

declare module 'react-router-dom' {
  // Augment FutureConfig so the app can opt into experimental react-router
  // runtime flags without casting. This merges with the library's types.
  interface FutureConfig {
    v7_preventBasepathDoubleSlash?: boolean;
    v7_useIdInRoutePaths?: boolean;
    v7_startTransition?: boolean;
    v7_relativeSplatPath?: boolean;
    [key: string]: unknown;
  }
}
