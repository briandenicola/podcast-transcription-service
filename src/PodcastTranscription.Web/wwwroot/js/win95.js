// Taskbar clock and connection indicator.
//
// Both are painted from a timer rather than by Blazor. A per-second render would be a network
// round trip per tick over the circuit, and — more to the point — the connection indicator has to
// keep working when there is no circuit at all, which is exactly when it matters.
(() => {
    const pad = (n) => String(n).padStart(2, '0');

    let connectionText = 'Connecting…';

    const clockText = () => {
        const now = new Date();
        const suffix = now.getHours() >= 12 ? 'PM' : 'AM';
        const hours = now.getHours() % 12 || 12;

        return `${hours}:${pad(now.getMinutes())} ${suffix}`;
    };

    // Repainted on every tick because Blazor replaces the DOM when it renders, which wipes
    // anything written here beforehand.
    const paint = () => {
        const clock = document.getElementById('w95-clock');
        if (clock) {
            clock.textContent = clockText();
        }

        const connection = document.getElementById('w95-connection');
        if (connection && connection.textContent !== connectionText) {
            connection.textContent = connectionText;
        }
    };

    // Interactive controls need a live Blazor circuit. When it never connects the page still
    // renders and anything driven by the circuit simply does nothing, which is impossible to
    // tell from a bug.
    //
    // This used to POST to _blazor/negotiate and call a 200 "connected", which was worse than
    // useless: negotiate is an ordinary HTTP request, so it succeeds in exactly the setup that
    // breaks the circuit — a proxy that forwards HTTP but not the websocket upgrade. It reported
    // "connected" on a deployment where nothing interactive worked at all. The only honest test
    // is to open the websocket, so that is what this does.
    const checkCircuit = () => {
        let settled = false;

        const settle = (text) => {
            if (settled) {
                return;
            }

            settled = true;
            connectionText = text;
            paint();
        };

        try {
            const url = new URL('_blazor', document.baseURI);
            url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';

            const socket = new WebSocket(url);

            // A proxy that swallows the upgrade often leaves the request hanging rather than
            // refusing it, so silence past this point counts as a failure.
            const timer = setTimeout(() => {
                settle('Server: no websocket');
                try { socket.close(); } catch { /* already gone */ }
            }, 8000);

            socket.onopen = () => {
                clearTimeout(timer);
                settle('Server: connected');

                // The handshake is SignalR's business; this only needed to know the upgrade works.
                try { socket.close(); } catch { /* already gone */ }
            };

            socket.onerror = () => {
                clearTimeout(timer);
                settle('Server: no websocket');
            };

            // Reached without onopen having fired means the upgrade never completed, however
            // politely it was refused. If it did open, settle() has already run and this is a
            // no-op — the close is just this probe hanging up after a successful test.
            socket.onclose = () => {
                clearTimeout(timer);
                settle('Server: no websocket');
            };
        } catch (error) {
            settle('Server: no websocket');
        }
    };

    paint();
    setInterval(paint, 2000);
    checkCircuit();
})();
