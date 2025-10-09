import React from 'react';

export const Example = ({ obj }: { obj: unknown }) => {
  console.debug('raw object', obj);
  console.info('info message');
  return (
    <div>
      <pre>{JSON.stringify(obj, null, 2)}</pre>
      <span>{JSON.stringify(obj)}</span>
    </div>
  );
};
