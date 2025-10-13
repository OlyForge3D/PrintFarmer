import React from 'react';
import { BrowserRouter } from 'react-router-dom';

// Shared test router to centralize react-router future flags used in tests.
export function TestRouter({ children }: { children: React.ReactNode }) {
  return (
    <BrowserRouter
      future={{
        v7_preventBasepathDoubleSlash: true,
        v7_useIdInRoutePaths: true,
        v7_startTransition: true,
        v7_relativeSplatPath: true,
      }}
    >
      {children}
    </BrowserRouter>
  );
}
