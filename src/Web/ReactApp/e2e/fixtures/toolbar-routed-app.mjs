import React from 'react';
import { createRoot } from 'react-dom/client';
import App from '/src/App.tsx';
import { SpoolmanProvider } from '/src/contexts/SpoolmanContext.tsx';
import '/src/index.css';
import '/src/styles/icons.css';

createRoot(document.getElementById('root')).render(
  React.createElement(SpoolmanProvider, null, React.createElement(App)),
);
