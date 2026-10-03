const {test} = require('node:test');
const assert = require('node:assert/strict');
const {readFileSync} = require('node:fs');
const {join} = require('node:path');
const {runInNewContext} = require('node:vm');

// Exercise the real wrapper with controlled viewport changes and browser scheduling.
function createHarness() {
    const viewport = {clientWidth: 800, clientHeight: 400};
    const frames = new Map();
    const observers = [];
    let frameId = 0;

    class Terminal {
        constructor() {
            this.output = '';
            this.disposed = false;
        }

        loadAddon(addon) {
            this.addon = addon;
        }

        open(container) {
            this.container = container;
        }

        write(data) {
            this.output += data;
        }

        reset() {
            this.output = '';
        }

        dispose() {
            this.disposed = true;
        }
    }

    class FitAddon {
        constructor() {
            this.calls = 0;
        }

        fit() {
            this.calls++;
            // xterm changes its children during fitting, which can notify the observer again.
            observers.at(-1).notify();
        }
    }

    class ResizeObserver {
        constructor(callback) {
            this.callback = callback;
            observers.push(this);
        }

        observe() {
        }

        disconnect() {
            this.disconnected = true;
        }

        notify() {
            if (!this.disconnected) this.callback();
        }
    }

    const context = {
        window: {}, Terminal, FitAddon: {FitAddon}, ResizeObserver,
        document: {getElementById: () => viewport},
        requestAnimationFrame: callback => {
            frames.set(++frameId, callback);
            return frameId;
        },
        cancelAnimationFrame: id => frames.delete(id)
    };
    runInNewContext(readFileSync(join(__dirname, '../../Web/wwwroot/js/xterm-wrapper.js'), 'utf8'), context);
    return {
        wrapper: context.window.xtermWrapper, viewport, observers, frames,
        flush() {
            const pending = [...frames.values()];
            frames.clear();
            for (const callback of pending) callback();
        }
    };
}

test('fitting does not repeat when xterm changes children inside an unchanged viewport', () => {
    const h = createHarness();
    h.wrapper.init('history');
    h.flush();
    const entry = h.wrapper.terminals.history;
    for (let i = 0; i < 100; i++) {
        h.observers[0].notify();
        h.flush();
    }
    assert.equal(entry.fitAddon.calls, 1);
    assert.equal(h.viewport.clientHeight, 400);
    assert.equal(h.frames.size, 0);
});

test('external resizes coalesce and hidden terminals fit when made visible', () => {
    const h = createHarness();
    h.viewport.clientHeight = 0;
    h.wrapper.init('history');
    h.flush();
    const entry = h.wrapper.terminals.history;
    assert.equal(entry.fitAddon.calls, 0);
    h.viewport.clientHeight = 400;
    for (let i = 0; i < 100; i++) h.observers[0].notify();
    assert.equal(h.frames.size, 1);
    h.flush();
    h.flush();
    assert.equal(entry.fitAddon.calls, 1);
    h.viewport.clientWidth = 600;
    h.observers[0].notify();
    h.flush();
    h.flush();
    assert.equal(entry.fitAddon.calls, 2);
});

test('history reset and replay preserve the pending initial fit and replace output', () => {
    const h = createHarness();
    h.wrapper.init('history');
    h.wrapper.write('history', 'old deployment');
    h.wrapper.clear('history');
    h.wrapper.write('history', 'saved build\r\nsaved container\r\n');
    h.flush();
    h.flush();
    const entry = h.wrapper.terminals.history;
    assert.equal(entry.term.output, 'saved build\r\nsaved container\r\n');
    assert.equal(entry.fitAddon.calls, 1);
});

test('disposal and reinitialization release observers, frames and old terminals', () => {
    const h = createHarness();
    h.wrapper.init('history');
    const old = h.wrapper.terminals.history;
    h.wrapper.init('history');
    assert.equal(old.term.disposed, true);
    assert.equal(old.resizeObserver.disconnected, true);
    assert.equal(h.frames.size, 1);
    const current = h.wrapper.terminals.history;
    h.wrapper.dispose('history');
    h.flush();
    assert.equal(current.term.disposed, true);
    assert.equal(current.fitAddon.calls, 0);
    assert.equal(h.frames.size, 0);
    assert.equal(h.wrapper.terminals.history, undefined);
});
