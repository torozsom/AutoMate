/**
 * Owns xterm instances and their viewport lifetimes, keyed by mounted host ID.
 * @namespace
 */
window.xtermWrapper = {
    /** Owned terminal entries keyed by mounted host ID. */
    terminals: {},

    /**
     * Replaces an existing terminal and fits its viewport through coalesced resize observations.
     * @param {string} elementId - ID of a mounted terminal host element.
     * @returns {void} No value; dispose releases the terminal and resize observer.
     * @throws {Error} The xterm dependencies or mounted host are unavailable.
     */
    init: function (elementId) {
        this.dispose(elementId);

        const term = new Terminal({
            theme: {background: '#07090e', foreground: '#bbc9cf', cursor: '#00d2ff', selectionBackground: '#003543'},
            convertEol: true,
            cursorBlink: true,
            fontFamily: '"JetBrains Mono", Consolas, "Courier New", monospace',
            fontSize: 13,
            lineHeight: 1.4
        });

        const fitAddon = new FitAddon.FitAddon();
        term.loadAddon(fitAddon);

        const container = document.getElementById(elementId);
        term.open(container);

        // Fitting changes xterm's children. Only fit again when the external viewport changes, and do it outside
        // the ResizeObserver callback to avoid a content/resize feedback loop.
        const entry = {term, fitAddon, resizeObserver: null, frame: null, width: 0, height: 0};

        /**
         * Schedules fitting only when no viewport update is already pending.
         * @returns {void} No value.
         */
        const fit = () => {
            if (entry.frame !== null) return;
            entry.frame = requestAnimationFrame(
                /**
                 * Fits a visible viewport only after its external dimensions change.
                 * @returns {void} No value; zero-size or unchanged viewports are ignored.
                 */
                () => {
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

    /**
     * Writes terminal text when the identified instance exists.
     * @param {string} elementId - Initialized terminal host ID.
     * @param {string} data - Terminal output, including supported escape sequences.
     * @returns {void} No value; missing instances are ignored.
     */
    write: function (elementId, data) {
        if (this.terminals[elementId]) {
            this.terminals[elementId].term.write(data);
        }
    },

    /**
     * Resets output and terminal state when the instance exists.
     * @param {string} elementId - Initialized terminal host ID.
     * @returns {void} No value; missing instances are ignored.
     */
    clear: function (elementId) {
        if (this.terminals[elementId]) {
            this.terminals[elementId].term.reset();
        }
    },

    /**
     * Cancels pending fitting, disconnects resize observation and releases the terminal.
     * @param {string} elementId - Terminal host ID to remove.
     * @returns {void} No value; missing instances are ignored.
     */
    dispose: function (elementId) {
        if (this.terminals[elementId]) {
            if (this.terminals[elementId].frame !== null)
                cancelAnimationFrame(this.terminals[elementId].frame);

            this.terminals[elementId].resizeObserver.disconnect();
            this.terminals[elementId].term.dispose();

            delete this.terminals[elementId];
        }
    }
};
