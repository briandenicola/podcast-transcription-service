// Summarisation progress, polled over ordinary HTTP.
//
// Summarising a long episode is several minutes of model time, and the page that started it has
// no live connection to the run — deliberately, because a Blazor circuit is exactly what does
// not work in every deployment. So the card carries the ids it needs in data attributes, and
// this asks the server how far along the run is until it finishes, then reloads to show it.
(() => {
    const POLL_MS = 2000;

    const card = () => document.getElementById('summary-card');

    const poll = async () => {
        const el = card();
        if (!el || el.dataset.running !== 'true') {
            return;
        }

        const episode = el.dataset.episode;
        const transcript = el.dataset.transcript;

        let status;
        try {
            const response = await fetch(
                `/episodes/${episode}/summary-status?transcriptId=${transcript}`,
                { headers: { 'Accept': 'application/json' } });

            if (!response.ok) {
                return setTimeout(poll, POLL_MS);
            }

            status = await response.json();
        } catch {
            // A blip on the network is not a reason to stop watching.
            return setTimeout(poll, POLL_MS);
        }

        if (!status.running) {
            // Finished, one way or the other. The server renders the summary — or the failure —
            // far better than this could patch it in.
            window.location.reload();
            return;
        }

        const bar = document.getElementById('summary-bar');
        const stage = document.getElementById('summary-stage');
        const elapsed = document.getElementById('summary-elapsed');

        if (bar) {
            bar.style.width = `${status.percent}%`;
            bar.parentElement?.setAttribute('aria-valuenow', String(status.percent));
        }

        // The stage already carries its own count ("Reading part 2 of 5"), so appending a pass
        // number only produced the likes of "pass 0 of 6". The bar carries the overall share.
        if (stage && status.stage) {
            stage.textContent = status.stage;
        }

        if (elapsed) {
            elapsed.textContent = `${status.elapsedSeconds}s`;
        }

        setTimeout(poll, POLL_MS);
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', poll);
    } else {
        poll();
    }
})();
