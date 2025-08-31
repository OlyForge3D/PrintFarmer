// Shared SVG Icons for PrintFarmer Application
window.Icons = {
    // Window-style maximize (expand) icon
    maximize: `<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
        <path d="M3 3h18v18H3V3zm2 2v14h14V5H5z"/>
        <path d="M7 7h10v10H7V7z"/>
    </svg>`,
    
    // Window-style minimize (collapse) icon  
    minimize: `<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
        <path d="M5 12h14v2H5z"/>
    </svg>`,
    
    // Edit/pencil icon
    edit: `<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
        <path d="M3 17.25V21h3.75L20.81 6.94l-3.75-3.75L3 17.25zm2.92 1.33l-.5-1.99 9.9-9.9 1.99 1.99-9.9 9.9z"/>
        <path d="M18.37 3.87a1.25 1.25 0 011.77 1.77l-1.06 1.06-1.77-1.77 1.06-1.06z"/>
    </svg>`,
    
    // External link icon
    externalLink: `<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
        <path d="M14 3h7v7h-2V6.41l-9.29 9.3-1.42-1.42 9.3-9.29H14V3z"/>
        <path d="M5 5h6V3H5c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h12c1.1 0 2-.9 2-2v-6h-2v6H5V5z"/>
    </svg>`,
    
    // Pause icon
    pause: `<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
        <path d="M7 5h3v14H7zM14 5h3v14h-3z"/>
    </svg>`,
    
    // Play/Resume icon
    play: `<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
        <path d="M8 5v14l11-7z"/>
    </svg>`,
    
    // Emergency stop/warning icon
    emergencyStop: `<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
        <path d="M12 2L2 22h20L12 2zm-1 7h2v6h-2V9zm0 8h2v2h-2v-2z"/>
    </svg>`,
    
    // Camera icon
    camera: `<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
        <path d="M17 10.5V7a2 2 0 00-2-2H5a2 2 0 00-2 2v10a2 2 0 002 2h10a2 2 0 002-2v-3.5l4 4v-11l-4 4z"/>
    </svg>`,
    
    // Hide camera icon (eye-slash)
    cameraHide: `<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
        <path d="M3.28 2.22a.75.75 0 0 0-1.06 1.06l18 18a.75.75 0 1 0 1.06-1.06l-18-18ZM9.88 14.12a2 2 0 0 1-2.83 0 1.65 1.65 0 0 1 0-2.34l2.83-2.83a2 2 0 0 1 2.83 0 1.65 1.65 0 0 1 0 2.34L9.88 14.12ZM12 5.5c4.14 0 7.5 3.36 7.5 7.5 0 .85-.15 1.66-.41 2.42l1.49 1.49C21.35 15.73 22 14.41 22 12.5 22 6.98 17.52 2.5 12 2.5c-1.41 0-2.73.59-3.91 1.42l1.49 1.49C10.34 5.65 11.15 5.5 12 5.5ZM2 3.77l1.27-1.27L20.23 19.46 19 20.73l-2.68-2.68C15.06 19.83 13.5 20.5 12 20.5c-5.52 0-10-4.48-10-10 0-2.21 1.56-4.17 3.18-5.55L2 3.77Z"/>
    </svg>`,
    
    // Delete/trash icon
    delete: `<svg xmlns="http://www.w3.org/2000/svg" width="16" height="16" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
        <path d="M6 19c0 1.1.9 2 2 2h8c1.1 0 2-.9 2-2V7H6v12zM19 4h-3.5l-1-1h-5l-1 1H5v2h14V4z"/>
    </svg>`,
    
    // Temperature/thermometer icon
    temperature: `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
        <path d="M15 14.76V5a3 3 0 10-6 0v9.76a5 5 0 106 0zM10 5a2 2 0 114 0v9.76l.2.2A3 3 0 1110 15l.2-.24V5z"/>
        <path d="M12 17a1 1 0 001-1V8a1 1 0 10-2 0v8a1 1 0 001 1z"/>
    </svg>`
};

// Simple helper: render all [data-icon] placeholders with matching SVG from window.Icons
window.Icons.renderAll = function(){
    try {
        var nodes = document.querySelectorAll('[data-icon]');
        nodes.forEach(function(el){
            var name = el.getAttribute('data-icon');
            if (name && window.Icons[name]) {
                // Only render if empty or different to avoid thrashing
                if (el.innerHTML.trim() !== window.Icons[name].trim()) {
                    el.innerHTML = window.Icons[name];
                }
            }
        });
    } catch {}
};
