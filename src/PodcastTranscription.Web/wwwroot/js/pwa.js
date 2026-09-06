(() => {
    if (!('serviceWorker' in navigator)) {
        return;
    }

    window.addEventListener('load', () => {
        const serviceWorkerUrl = new URL('service-worker.js', document.baseURI);
        navigator.serviceWorker.register(serviceWorkerUrl, {
            scope: new URL('.', document.baseURI).pathname
        }).catch((error) => {
            console.error('Service worker registration failed.', error);
        });
    });
})();
