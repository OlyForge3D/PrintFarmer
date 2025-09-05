// Simple helper to trigger the browser's native date picker
window.PF = window.PF || {};
window.PF.openDatePicker = function (input) {
  try {
    if (!input) return;
    // Focus, then attempt to show picker if supported
    input.focus();
    if (typeof input.showPicker === 'function') {
      input.showPicker();
    }
  } catch (_) {
    // no-op
  }
};
