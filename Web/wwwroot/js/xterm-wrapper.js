/**
 * A wrapper for xterm.js to manage multiple terminal instances.
 * @type {{terminals: {}, init: function(*): void, write: function(*, *): void, dispose: function(*): void}}
 */
window.xtermWrapper = {
    terminals: {},

    init: function (elementId) {
        const term = new Terminal({
            theme: {background: '#1e1e1e'},
            convertEol: true,
            cursorBlink: true,
            fontFamily: 'Consolas, "Courier New", monospace'
        });

        const fitAddon = new FitAddon.FitAddon();
        term.loadAddon(fitAddon);

        const container = document.getElementById(elementId);
        term.open(container);
        fitAddon.fit();

        const resizeObserver = new ResizeObserver(() => {
            fitAddon.fit();
        });
        resizeObserver.observe(container);

        this.terminals[elementId] = {term, fitAddon, resizeObserver};
    },

    write: function (elementId, data) {
        if (this.terminals[elementId]) {
            this.terminals[elementId].term.write(data);
        }
    },

    dispose: function (elementId) {
        if (this.terminals[elementId]) {
            this.terminals[elementId].resizeObserver.disconnect();
            this.terminals[elementId].term.dispose();
            delete this.terminals[elementId];
        }
    }
};