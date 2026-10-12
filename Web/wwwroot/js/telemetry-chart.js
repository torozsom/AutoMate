// Client-side inspection avoids a server roundtrip for every pointer movement.
/** Listener and observer registrations keyed by chart root. */
const bindings = new WeakMap();


/**
 * Replaces chart inspection handlers; points are resolved after each Blazor update.
 * @param {HTMLElement} root - Mounted telemetry chart root.
 * @returns {void} No value; detach releases the registered listeners and observer.
 */
export function attach(root) {
    detach(root);
    let selected = -1;
    /**
     * Selects an observed point and updates the accessible inspection text.
     * @param {number} index - Requested point position, clamped to available points.
     * @returns {void} No value; an empty chart is unchanged.
     */
    const show = index => {
        const points = [...root.querySelectorAll('[data-detail]')];
        if (!points.length) return;
        selected = Math.max(0, Math.min(index, points.length - 1));
        points.forEach(
            /**
             * Updates one point highlight for the current selection.
             * @param {Element} point - Rendered chart point.
             * @param {number} i - Point position.
             * @returns {boolean} Whether the inspected class is present after toggling.
             */
            (point, i) => point.classList.toggle('is-inspected', i === selected));
        const output = root.querySelector('[data-inspection]');
        if (output) output.textContent = points[selected].dataset.detail;
    };
    /**
     * Inspects a targeted point or the nearest point to a plot tap.
     * @param {PointerEvent} event - Delegated pointer event.
     * @returns {void} No value; no server request is made.
     */
    const pointer = event => {
        const points = [...root.querySelectorAll('[data-detail]')];
        const point = event.target.closest?.('[data-detail]');
        if (point) show(points.indexOf(point));
        else if (event.type === 'pointerdown' && event.target.closest?.('svg') && points.length) {
            // A tap anywhere in the plot selects the closest point, including isolated observations.
            let nearest = 0, distance = Infinity;
            points.forEach(
                /**
                 * Compares one point with the tap and retains the nearest candidate.
                 * @param {Element} candidate - Rendered chart point.
                 * @param {number} index - Candidate position.
                 * @returns {void} No value; updates the enclosing nearest-point state.
                 */
                (candidate, index) => {
                    const box = candidate.getBoundingClientRect();
                    const delta = Math.hypot(event.clientX - box.x - box.width / 2, event.clientY - box.y - box.height / 2);
                    if (delta < distance) {
                        nearest = index;
                        distance = delta;
                    }
                });
            show(nearest);
        }
    };
    /**
     * Navigates observed points using arrow, Home and End keys.
     * @param {KeyboardEvent} event - Chart key event.
     * @returns {void} No value; unrelated keys retain native behavior.
     */
    const key = event => {
        if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
        event.preventDefault();
        show(event.key === 'Home' ? 0 : event.key === 'End' ? Number.MAX_SAFE_INTEGER : selected < 0 ? 0 : selected + (event.key === 'ArrowRight' ? 1 : -1));
    };
    root.addEventListener('pointerover', pointer);
    root.addEventListener('pointerdown', pointer);
    root.addEventListener('keydown', key);
    // Keep axis text legible when an SVG scales down on narrow screens or at high text zoom.
    const observer = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(
        /**
         * Adjusts SVG axis label sizes after the chart viewport changes.
         * @returns {void} No value; absent or zero-size plots are unchanged.
         */
        () => {
            const svg = root.querySelector('svg');
            if (!svg) return;
            const box = svg.getBoundingClientRect();
            const scale = Math.min(box.width / 480, box.height / 250);
            if (scale > 0) root.querySelectorAll('.telemetry-axis-label').forEach(
                /**
                 * Keeps one axis label legible at the current SVG scale.
                 * @param {SVGElement} label - Axis label to resize.
                 * @returns {void} No value.
                 */
                label => {
                    label.style.fontSize = `${12 / scale}px`;
                });
        });
    observer?.observe(root);
    bindings.set(root, {pointer, key, observer});
}


/**
 * Removes chart listeners and its resize observer when the component is disposed.
 * @param {HTMLElement} root - Chart root previously passed to attach.
 * @returns {void} No value; unregistered roots are ignored.
 */
export function detach(root) {
    const binding = bindings.get(root);
    if (!binding) return;
    root.removeEventListener('pointerover', binding.pointer);
    root.removeEventListener('pointerdown', binding.pointer);
    root.removeEventListener('keydown', binding.key);
    binding.observer?.disconnect();
    bindings.delete(root);
}
