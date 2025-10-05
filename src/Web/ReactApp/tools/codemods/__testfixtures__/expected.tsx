import React from 'react';

export const Example = ({ obj }: { obj: unknown }) => {
  if (window.PrintFarmerDebug?.toolsCodemodTest) {
    try { console.debug('raw object', obj); } catch { /* ignore debug stringify errors */ }
  }
  if (window.PrintFarmerDebug?.toolsCodemodTest) {
    try { console.info('info message'); } catch { /* ignore debug stringify errors */ }
  }
  return (
    <div>
      <pre>{renderUnknown(obj)}</pre>
      <span>{renderUnknown(obj)}</span>
    </div>
  );
};
