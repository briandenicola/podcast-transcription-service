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
    // renders and the buttons simply do nothing, which is impossible to tell from a bug. The
    // negotiate endpoint is what the circuit itself calls first, so its answer is the diagnosis.
    const checkCircuit = async () => {
        try {
            const response = await fetch('_blazor/negotiate?negotiateVersion=1', { method: 'POST' });

            connectionText = response.ok
                ? 'Server: connected'
                : `Server: NOT connected (HTTP ${response.status})`;
        } catch (error) {
            connectionText = 'Server: NOT connected';
        }

        paint();
    };

    paint();
    setInterval(paint, 2000);
    checkCircuit();
})();
