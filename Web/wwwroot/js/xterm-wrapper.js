/**
 * A wrapper for xterm.js to manage multiple terminal instances.
 * @type {{terminals: {}, init: function(*): void, write: function(*, *): void, dispose: function(*): void}}
 */
window.xtermWrapper = {
    terminals: {},

    init: function (elementId) {
        this.dispose(elementId);
        const term = new Terminal({
            theme: {background: '#07090e', foreground: '#bbc9cf', cursor: '#00d2ff', selectionBackground: '#003543'},
            convertEol: true,
            cursorBlink: true,
            fontFamily: '"JetBrains Mono", Consolas, "Courier New", monospace',
            fontSize: 12,
            lineHeight: 1.4
        });

        const fitAddon = new FitAddon.FitAddon();
        term.loadAddon(fitAddon);

        const container = document.getElementById(elementId);
        term.open(container);
        // Fitting changes xterm's children. Only fit again when the external viewport changes, and do it outside
        // the ResizeObserver callback to avoid a content/resize feedback loop.
        const entry = {term, fitAddon, resizeObserver: null, frame: null, width: 0, height: 0};
        const fit = () => {
            if (entry.frame !== null) return;
            entry.frame = requestAnimationFrame(() => {
                entry.frame = null;
                const width = container.clientWidth;
                const height = container.clientHeight;
                if (width <= 0 || height <= 0 || (width === entry.width && height === entry.height)) return;
                entry.width = width;
                entry.height = height;
                fitAddon.fit();
            });
        };
        const resizeObserver = new ResizeObserver(fit);
        entry.resizeObserver = resizeObserver;
        resizeObserver.observe(container);
        this.terminals[elementId] = entry;
        fit();
    },

    write: function (elementId, data) {
        if (this.terminals[elementId]) {
            this.terminals[elementId].term.write(data);
        }
    },

    clear: function (elementId) {
        if (this.terminals[elementId]) {
            this.terminals[elementId].term.reset();
        }
    },

    dispose: function (elementId) {
        if (this.terminals[elementId]) {
            if (this.terminals[elementId].frame !== null) cancelAnimationFrame(this.terminals[elementId].frame);
            this.terminals[elementId].resizeObserver.disconnect();
            this.terminals[elementId].term.dispose();
            delete this.terminals[elementId];
        }
    }
};
