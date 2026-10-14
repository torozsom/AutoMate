const {test} = require('node:test');
const assert = require('node:assert/strict');
const {readFileSync} = require('node:fs');
const {join} = require('node:path');
const {runInNewContext} = require('node:vm');

// Exercise the real wrapper with controlled viewport changes and browser scheduling.
/**
 * Loads the real terminal wrapper with controlled browser scheduling and viewport fakes.
 * @returns {object} Wrapper, viewport, observers, pending frames and a flush operation.
 */
function createHarness() {
    const viewport = {clientWidth: 800, clientHeight: 400};
    const frames = new Map();
    const observers = [];
    let frameId = 0;

    /**
     * Records terminal output and lifecycle without creating a real xterm instance.
     * @class
     */
    class Terminal {
        /**
         * Initializes the synthetic terminal output and lifecycle state.
         */
        constructor() {
            this.output = '';
            this.disposed = false;
        }

        /**
         * Stores the addon supplied by the terminal wrapper.
         * @param {object} addon - Synthetic fit addon supplied by the wrapper.
         * @returns {void} No value.
         */
        loadAddon(addon) {
            this.addon = addon;
        }

        /**
         * Stores the synthetic host supplied by the terminal wrapper.
         * @param {object} container - Controlled terminal viewport.
         * @returns {void} No value.
         */
        open(container) {
            this.container = container;
        }

        /**
         * Appends the supplied output to the synthetic terminal buffer.
         * @param {string} data - Terminal output to append.
         * @returns {void} No value.
         */
        write(data) {
            this.output += data;
        }

        /**
         * Clears captured output while retaining the terminal instance.
         * @returns {void} No value.
         */
        reset() {
            this.output = '';
        }

        /**
         * Records terminal disposal for lifecycle assertions.
         * @returns {void} No value.
         */
        dispose() {
            this.disposed = true;
        }
    }

    /**
     * Counts fitting and models xterm child changes by notifying the active observer.
     * @class
     */
    class FitAddon {
        /**
         * Initializes the fixture fit counter.
         */
        constructor() {
            this.calls = 0;
        }

        /**
         * Counts a fit and triggers the active observer to model child-layout feedback.
         * @returns {void} No value.
         */
        fit() {
            this.calls++;
            // xterm changes its children during fitting, which can notify the observer again.
            observers.at(-1).notify();
        }
    }

    /**
     * Records resize notifications and disconnect state for the terminal fixture.
     * @class
     */
    class ResizeObserver {
        /**
         * Stores the resize callback and records this observer instance.
         * @param {function(): void} callback - Controlled resize or animation-frame callback.
         */
        constructor(callback) {
            this.callback = callback;
            observers.push(this);
        }

        /**
         * Accepts observation without scheduling automatic resize notifications.
         * @returns {void} No value.
         */
        observe() {
        }

        /**
         * Marks the observer as disconnected.
         * @returns {void} No value.
         */
        disconnect() {
            this.disconnected = true;
        }

        /**
         * Invokes the configured resize callback only while observation remains connected.
         * @returns {void} No value.
         */
        notify() {
            if (!this.disconnected) this.callback();
        }
    }

    const context = {
        window: {}, Terminal, FitAddon: {FitAddon}, ResizeObserver,
        document: {
            /**
             * Supplies the same controlled viewport for terminal host lookup.
             * @returns {object} Synthetic viewport dimensions.
             */
            getElementById: () => viewport
        },

        /**
         * Queues a callback under an increasing fixture frame identity.
         * @param {function(): void} callback - Controlled resize or animation-frame callback.
         * @returns {number} Identity used to cancel this pending frame.
         */
        requestAnimationFrame: callback => {
            frames.set(++frameId, callback);
            return frameId;
        },

        /**
         * Removes the selected callback from pending fixture frames.
         * @param {number} id - Pending frame identity.
         * @returns {boolean} True when a pending frame was removed.
         */
        cancelAnimationFrame: id => frames.delete(id)
    };
    runInNewContext(readFileSync(join(__dirname, '../../../Web/wwwroot/js/xterm-wrapper.js'), 'utf8'), context);
    return {
        wrapper: context.window.xtermWrapper, viewport, observers, frames,
        /**
         * Runs a snapshot of pending frame callbacks after clearing the queue.
         * @returns {void} No value; newly scheduled frames await another flush.
         */
        flush() {
            const pending = [...frames.values()];
            frames.clear();
            for (const callback of pending) callback();
        }
    };
}

test('fitting does not repeat when xterm changes children inside an unchanged viewport',
    /**
     * Verifies that fitting does not repeat when xterm changes children inside an unchanged viewport.
     * @returns {void} No value; failed assertions throw.
     * @throws {Error} A fixture assertion or awaited browser operation fails.
     */
    () => {
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

test('external resizes coalesce and hidden terminals fit when made visible',
    /**
     * Verifies that external resizes coalesce and hidden terminals fit when made visible.
     * @returns {void} No value; failed assertions throw.
     * @throws {Error} A fixture assertion or awaited browser operation fails.
     */
    () => {
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

test('history reset and replay preserve the pending initial fit and replace output',
    /**
     * Verifies that history reset and replay preserve the pending initial fit and replace output.
     * @returns {void} No value; failed assertions throw.
     * @throws {Error} A fixture assertion or awaited browser operation fails.
     */
    () => {
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

test('disposal and reinitialization release observers, frames and old terminals',
    /**
     * Verifies that disposal and reinitialization release observers, frames and old terminals.
     * @returns {void} No value; failed assertions throw.
     * @throws {Error} A fixture assertion or awaited browser operation fails.
     */
    () => {
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
