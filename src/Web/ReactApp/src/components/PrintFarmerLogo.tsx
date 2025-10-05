import React from 'react';

export const PrintFarmerLogo: React.FC<{ className?: string; size?: number }> = ({ className = '', size = 48 }) => (
  <img
    src="/printfarmer-logo.svg"
    alt="PrintFarmer Logo"
    width={size}
    height={size}
    className={`${className} align-middle inline-block`}
    draggable={false}
  />
);
