import React from 'react';
import { useNavigate } from 'react-router-dom';

export function useRegistrationPending() {
  const navigate = useNavigate();
  React.useEffect(() => {
    navigate('/registration-pending', { replace: true });
  }, [navigate]);
}
