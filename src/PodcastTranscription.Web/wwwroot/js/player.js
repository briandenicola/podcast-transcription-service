// Transcript player.
//
// Highlighting runs entirely in the browser. A Blazor Server circuit is a network round trip per
// event, and `timeupdate` fires several times a second — pushing that through the circuit would
// make the highlight lag the audio and flood the connection for no benefit. The server renders
// the transcript once with timings in data attributes; everything after that is local.

window.podcastPlayer = (() => {
    let audio = null;
    let container = null;
    let cues = null;
    let segments = [];
    let words = [];
    let activeSegment = null;
    let activeWord = null;

    // Timed elements, sorted by start, so the current one can be found by binary search rather
    // than by walking 13,000 words on every tick.
    const collect = (selector) =>
        Array.from(container.querySelectorAll(selector))
            .map((el) => ({
                el,
                t0: Number(el.dataset.t0),
                t1: Number(el.dataset.t1)
            }))
            .filter((item) => Number.isFinite(item.t0) && Number.isFinite(item.t1))
            .sort((a, b) => a.t0 - b.t0);

    const findAt = (items, ms) => {
        let low = 0;
        let high = items.length - 1;
        let found = null;

        while (low <= high) {
            const mid = (low + high) >> 1;
            const item = items[mid];

            if (ms < item.t0) {
                high = mid - 1;
            } else if (ms >= item.t1) {
                low = mid + 1;
            } else {
                found = item;
                break;
            }
        }

        // Between two items — a pause, or drift at a chunk seam — so keep the one just passed
        // rather than dropping the highlight entirely.
        if (!found && high >= 0 && high < items.length) {
            found = items[high];
        }

        return found;
    };

    const setActive = (current, next, className) => {
        if (current === next) {
            return current;
        }

        if (current) {
            current.el.classList.remove(className);
        }

        if (next) {
            next.el.classList.add(className);
        }

        return next;
    };

    const onTimeUpdate = () => {
        const ms = audio.currentTime * 1000;

        const segment = findAt(segments, ms);
        if (segment !== activeSegment) {
            activeSegment = setActive(activeSegment, segment, 'is-active');

            if (segment && container.dataset.follow === 'true') {
                segment.el.scrollIntoView({ block: 'center', behavior: 'smooth' });
            }
        }

        if (words.length > 0) {
            activeWord = setActive(activeWord, findAt(words, ms), 'is-active-word');
        }
    };

    const seekAndPlay = (ms) => {
        if (!Number.isFinite(ms)) {
            return;
        }

        audio.currentTime = ms / 1000;
        audio.play().catch(() => { /* autoplay refused; the seek still happened */ });
    };

    const clickHandlerFor = (root) => (event) => {
        const target = event.target.closest('[data-t0]');
        if (!target || !root.contains(target)) {
            return;
        }

        seekAndPlay(Number(target.dataset.t0));
    };

    // One delegated listener rather than thousands: any element carrying a start time seeks to it.
    let onClick = null;

    // The same, for the [hh:mm:ss] the summary cites. It sits outside the transcript, so it
    // needs its own listener — but pressing one should do exactly what clicking a line does.
    let onCueClick = null;

    const onCueKey = (event) => {
        if (event.key !== 'Enter' && event.key !== ' ') {
            return;
        }

        const target = event.target.closest('[data-t0]');
        if (!target || !cues.contains(target)) {
            return;
        }

        // The cues are anchors without an href, so the browser gives them no keyboard
        // activation of their own.
        event.preventDefault();
        seekAndPlay(Number(target.dataset.t0));
    };

    return {
        init(audioId, containerId, cueContainerId) {
            const nextAudio = document.getElementById(audioId);
            const nextContainer = document.getElementById(containerId);
            if (!nextAudio || !nextContainer) {
                return;
            }

            // Blazor re-renders the transcript when toggles change, so listeners are rebound
            // against the new DOM each time.
            this.dispose();

            audio = nextAudio;
            container = nextContainer;
            segments = collect('[data-segment]');
            words = collect('[data-word]');
            activeSegment = null;
            activeWord = null;

            onClick = clickHandlerFor(container);

            audio.addEventListener('timeupdate', onTimeUpdate);
            container.addEventListener('click', onClick);

            // Optional: the summary card is only on the page once there is a summary.
            cues = cueContainerId ? document.getElementById(cueContainerId) : null;
            if (cues) {
                onCueClick = clickHandlerFor(cues);
                cues.addEventListener('click', onCueClick);
                cues.addEventListener('keydown', onCueKey);
            }

            onTimeUpdate();
        },

        seek(ms) {
            if (!audio || !Number.isFinite(ms)) {
                return;
            }

            audio.currentTime = ms / 1000;
            onTimeUpdate();
        },

        dispose() {
            if (audio) {
                audio.removeEventListener('timeupdate', onTimeUpdate);
            }

            if (container && onClick) {
                container.removeEventListener('click', onClick);
            }

            if (cues && onCueClick) {
                cues.removeEventListener('click', onCueClick);
                cues.removeEventListener('keydown', onCueKey);
            }

            audio = null;
            container = null;
            cues = null;
            onClick = null;
            onCueClick = null;
            segments = [];
            words = [];
            activeSegment = null;
            activeWord = null;
        }
    };
})();
