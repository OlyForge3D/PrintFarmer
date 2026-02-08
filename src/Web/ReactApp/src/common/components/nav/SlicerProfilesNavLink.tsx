import React from 'react';
import { Link, useLocation } from 'react-router';

export const SlicerProfilesNavLink: React.FC = () => {
  const loc = useLocation();
  const active = loc.pathname.includes('/slicer-profiles');
  return (
    <Link
      to="/slicer-profiles"
      className={`px-3 py-2 rounded-sm text-sm font-medium ${active ? 'bg-pf-accent-bg text-white' : 'text-pf-text-primary hover:bg-pf-bg-1'}`}
    >
      Profiles
    </Link>
  );
};

export default SlicerProfilesNavLink;
