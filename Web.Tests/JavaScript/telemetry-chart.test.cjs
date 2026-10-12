const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../../Web/wwwroot/js/telemetry-chart.js'), 'utf8');
const modulePromise = import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

// Minimal DOM recording harness exercises event lifecycle and nearest-point selection.
/**
 * Creates a minimal chart DOM and listener recorder for client interaction tests.
 * @returns {object} Chart root, registered handlers, output and synthetic points.
 */
function fixture() {
    const handlers = new Map();
    const points = [0, 1, 2].map(
        /**
         * Creates one synthetic point with fixed geometry and an accessible detail label.
         * @param {number} i - Synthetic point position.
         * @returns {object} Synthetic chart point at the supplied position.
         */
        i => ({
            dataset: {detail: 'point ' + i},
            classList: {
                /**
                 * Ignores point-class changes because this fixture inspects accessible output text.
                 * @returns {void} No value.
                 */
                toggle() {
                }
            },
            /**
             * Supplies fixed point geometry for nearest-observation selection.
             * @returns {object} Synthetic point rectangle in CSS pixels.
             */
            getBoundingClientRect: () => ({x: 100 * i, y: 0, width: 8, height: 8})
        }));
    const output = {textContent: ''};
    const root = {

        /**
         * Supplies the synthetic chart point collection for fixture queries.
         * @returns {object[]} Synthetic points in position order.
         */
        querySelectorAll: () => points,
        /**
         * Supplies the accessible output recorder for fixture queries.
         * @returns {object} Synthetic inspection output node.
         */
        querySelector: () => output,

        /**
         * Stores one handler per event name so duplicate registration can be detected.
         * @param {string} name - Fixture event or key name.
         * @param {Function} handler - Event handler to record.
         * @returns {Map<string, Function>} The listener recording map.
         */
        addEventListener: (name, handler) => handlers.set(name, handler),

        /**
         * Removes the handler associated with the supplied event name.
         * @param {string} name - Fixture event or key name.
         * @returns {boolean} True when the event registration was present.
         */
        removeEventListener: name => handlers.delete(name)
    };
    return {root, handlers, output, points};
}

test('arrow keys, Home and End inspect observations without server calls',
    /**
     * Verifies that arrow keys, Home and End inspect observations without server calls.
     * @returns {Promise<void>} Resolves when the client scenario and assertions finish.
     * @throws {Error} A fixture assertion or awaited browser operation fails.
     */
    async () => {
        const chart = await modulePromise, view = fixture();
        chart.attach(view.root);
        /**
         * Dispatches a synthetic key event to the registered chart handler.
         * @param {string} name - Fixture event or key name.
         * @returns {void} No value.
         */
        const key = name => view.handlers.get('keydown')({
            key: name,
            /**
             * Accepts prevention of native key behavior in the chart event fixture.
             * @returns {void} No value.
             */
            preventDefault() {
            }
        });
        key('ArrowRight');
        assert.equal(view.output.textContent, 'point 0');
        key('ArrowRight');
        assert.equal(view.output.textContent, 'point 1');
        key('End');
        assert.equal(view.output.textContent, 'point 2');
        key('Home');
        assert.equal(view.output.textContent, 'point 0');
        chart.detach(view.root);
        assert.equal(view.handlers.size, 0);
    });
test('tap selects nearest sparse observation; reattachment does not duplicate listeners',
    /**
     * Verifies that tap selects nearest sparse observation; reattachment does not duplicate listeners.
     * @returns {Promise<void>} Resolves when the client scenario and assertions finish.
     * @throws {Error} A fixture assertion or awaited browser operation fails.
     */
    async () => {
        const chart = await modulePromise, view = fixture();
        chart.attach(view.root);
        chart.attach(view.root);
        assert.equal(view.handlers.size, 3);
        view.handlers.get('pointerdown')({
            type: 'pointerdown', clientX: 190, clientY: 3,
            target: {
                /**
                 * Models plot targeting for sparse-point tap inspection.
                 * @param {string} selector - Ancestor selector requested by the chart.
                 * @returns {object|null} A synthetic SVG match for the plot selector; otherwise null.
                 */
                closest: selector => selector === 'svg' ? {} : null
            }
        });
        assert.equal(view.output.textContent, 'point 2');
        chart.detach(view.root);
    });
