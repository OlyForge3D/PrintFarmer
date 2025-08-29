// Helper function to trigger click on an element by ID
window.clickElement = (elementId) => {
    const element = document.getElementById(elementId);
    if (element) {
        element.click();
    }
};

// Helper function to download a file from byte array
window.downloadFile = (fileName, contentType, byteArray) => {
    const blob = new Blob([byteArray], { type: contentType });
    const url = URL.createObjectURL(blob);
    
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName;
    document.body.appendChild(a);
    a.click();
    
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
};
