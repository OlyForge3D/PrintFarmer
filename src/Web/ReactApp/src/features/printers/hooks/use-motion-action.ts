import { useRef, useState } from 'react';

/** Tracks only the initiating control, independently of the shared printer lock. */
export function useMotionAction() {
  const [pending, setPending] = useState(false);
  const running = useRef(false);
  const run = async (action: () => void | Promise<void>) => {
    if (running.current) return;
    running.current = true;
    setPending(true);
    try { await action(); }
    finally {
      running.current = false;
      setPending(false);
    }
  };
  return { pending, run };
}
