import { useEffect } from 'react';

const MODAL_OPEN_BODY_CLASS = 'pf-modal-open';

let modalBodyClassCount = 0;

function addModalOpenBodyClass() {
  if (modalBodyClassCount === 0) {
    document.body.classList.add(MODAL_OPEN_BODY_CLASS);
  }

  modalBodyClassCount += 1;
}

function removeModalOpenBodyClass() {
  modalBodyClassCount = Math.max(0, modalBodyClassCount - 1);

  if (modalBodyClassCount === 0) {
    document.body.classList.remove(MODAL_OPEN_BODY_CLASS);
  }
}

export function useModalOpenBodyClass(isOpen: boolean) {
  useEffect(() => {
    if (!isOpen) {
      return;
    }

    addModalOpenBodyClass();

    return () => {
      removeModalOpenBodyClass();
    };
  }, [isOpen]);
}
