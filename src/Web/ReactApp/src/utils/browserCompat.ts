// Browser compatibility detection
const detectBrowser = () => {
  const userAgent = navigator.userAgent;
  const isVSCodeSimpleBrowser = userAgent.includes('Code/') || userAgent.includes('Electron/');
  const isModuleSupported = 'noModule' in document.createElement('script');
  
  console.log('User Agent:', userAgent);
  console.log('Is VS Code Simple Browser:', isVSCodeSimpleBrowser);
  console.log('ES Module Support:', isModuleSupported);
  
  if (isVSCodeSimpleBrowser && !isModuleSupported) {
    document.body.innerHTML = `
      <div style="padding: 20px; font-family: Arial, sans-serif;">
        <h1>Browser Compatibility Issue</h1>
        <p>VS Code Simple Browser detected with limited ES module support.</p>
        <p>Please open this application in a full browser like Chrome, Firefox, or Edge.</p>
        <p><strong>Working URL:</strong> <a href="http://localhost:8080" target="_blank">http://localhost:8080</a></p>
      </div>
    `;
    return false;
  }
  return true;
};

// Run compatibility check
if (typeof window !== 'undefined') {
  detectBrowser();
}

export default detectBrowser;
