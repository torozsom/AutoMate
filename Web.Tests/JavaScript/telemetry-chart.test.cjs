const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../../Web/wwwroot/js/telemetry-chart.js'), 'utf8');
const modulePromise = import('data:text/javascript;base64,' + Buffer.from(source).toString('base64'));

// Minimal DOM recording harness exercises event lifecycle and nearest-point selection.
function fixture() {
    const handlers = new Map();
    const points = [0, 1, 2].map(i => ({dataset: {detail: 'point ' + i},
        classList: {toggle() {}}, getBoundingClientRect: () => ({x: 100 * i, y: 0, width: 8, height: 8})}));
    const output = {textContent: ''};
    const root = {querySelectorAll: () => points, querySelector: () => output,
        addEventListener: (name, handler) => handlers.set(name, handler),
        removeEventListener: name => handlers.delete(name)};
    return {root, handlers, output, points};
}
test('arrow keys, Home and End inspect observations without server calls', async () => {
    const chart = await modulePromise, view = fixture();
    chart.attach(view.root);
    const key = name => view.handlers.get('keydown')({key: name, preventDefault() {}});
    key('ArrowRight'); assert.equal(view.output.textContent, 'point 0');
    key('ArrowRight'); assert.equal(view.output.textContent, 'point 1');
    key('End'); assert.equal(view.output.textContent, 'point 2');
    key('Home'); assert.equal(view.output.textContent, 'point 0');
    chart.detach(view.root); assert.equal(view.handlers.size, 0);
});
test('tap selects nearest sparse observation; reattachment does not duplicate listeners', async () => {
    const chart = await modulePromise, view = fixture();
    chart.attach(view.root); chart.attach(view.root);
    assert.equal(view.handlers.size, 3);
    view.handlers.get('pointerdown')({type: 'pointerdown', clientX: 190, clientY: 3,
        target: {closest: selector => selector === 'svg' ? {} : null}});
    assert.equal(view.output.textContent, 'point 2');
    chart.detach(view.root);
});
