// Upload progress.
//
// Progressive enhancement, deliberately: the form posts perfectly well on its own, and this only
// upgrades the experience when the script runs. If it does not, uploading still works — which is
// the whole reason uploading is a plain form post rather than a circuit-bound component.
//
// A podcast episode is tens or hundreds of megabytes. Without this the browser sits on the form
// with no indication anything is happening, which is indistinguishable from a broken button.
(() => {
    const form = document.querySelector('form[action="/upload/file"]');
    if (!form || !window.XMLHttpRequest || !window.FormData) {
        return;
    }

    const fileInput = form.querySelector('input[type="file"]');
    const submit = form.querySelector('button[type="submit"]');
    if (!fileInput || !submit) {
        return;
    }

    const panel = document.createElement('div');
    panel.className = 'upload-progress';
    panel.hidden = true;
    panel.innerHTML =
        '<div class="upload-progress-label">' +
        '<span class="upload-hourglass" aria-hidden="true"></span>' +
        '<span class="upload-progress-text">Uploading…</span></div>' +
        '<div class="progress"><div class="progress-bar" style="width:0%"></div></div>' +
        '<div class="upload-progress-detail form-text"></div>';
    form.appendChild(panel);

    const bar = panel.querySelector('.progress-bar');
    const label = panel.querySelector('.upload-progress-text');
    const detail = panel.querySelector('.upload-progress-detail');

    const size = (bytes) => {
        const units = ['B', 'KB', 'MB', 'GB'];
        let value = bytes;
        let unit = 0;
        while (value >= 1024 && unit < units.length - 1) {
            value /= 1024;
            unit++;
        }
        return `${value.toFixed(unit === 0 ? 0 : 1)} ${units[unit]}`;
    };

    form.addEventListener('submit', (event) => {
        if (!fileInput.files || fileInput.files.length === 0) {
            return; // Let the browser's own "required" validation handle it.
        }

        event.preventDefault();

        const request = new XMLHttpRequest();
        const data = new FormData(form);
        const total = fileInput.files[0].size;

        submit.disabled = true;
        panel.hidden = false;

        // The cursor is the other half of the signal: the page is busy, not broken.
        document.body.classList.add('is-busy');

        request.upload.addEventListener('progress', (progress) => {
            const done = progress.lengthComputable ? progress.loaded : 0;
            const percent = progress.lengthComputable ? Math.round((done / progress.total) * 100) : 0;

            bar.style.width = `${percent}%`;
            label.textContent = `Uploading… ${percent}%`;
            detail.textContent = `${size(done)} of ${size(total)}`;
        });

        // The transfer is done but the server is still hashing and probing the file, which for a
        // long episode is not instant.
        request.upload.addEventListener('load', () => {
            bar.style.width = '100%';
            label.textContent = 'Processing…';
            detail.textContent = 'Hashing the file and reading its duration.';
        });

        request.addEventListener('load', () => {
            // The endpoint redirects to the new episode; XHR follows it, so responseURL is where
            // the browser should end up.
            window.location.href = request.responseURL || '/';
        });

        request.addEventListener('error', () => {
            panel.hidden = true;
            submit.disabled = false;
            label.textContent = '';
            document.body.classList.remove('is-busy');
            window.alert('The upload failed to reach the server. Check the connection and try again.');
        });

        request.open('POST', form.getAttribute('action'), true);
        request.send(data);
    });
})();
