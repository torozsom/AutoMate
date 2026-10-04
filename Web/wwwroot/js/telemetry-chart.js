// Client-side inspection avoids a server roundtrip for every pointer movement.
const bindings = new WeakMap();
/** Installs delegated handlers; points are resolved afresh after Blazor updates. */
export function attach(root) {
    detach(root);
    let selected = -1;
    const show = index => {
        const points = [...root.querySelectorAll('[data-detail]')];
        if (!points.length) return;
        selected = Math.max(0, Math.min(index, points.length - 1));
        points.forEach((point, i) => point.classList.toggle('is-inspected', i === selected));
        const output = root.querySelector('[data-inspection]');
        if (output) output.textContent = points[selected].dataset.detail;
    };
    const pointer = event => {
        const points = [...root.querySelectorAll('[data-detail]')];
        const point = event.target.closest?.('[data-detail]');
        if (point) show(points.indexOf(point));
        else if (event.type === 'pointerdown' && event.target.closest?.('svg') && points.length) {
            // A tap anywhere in the plot selects the closest point, including isolated observations.
            let nearest = 0, distance = Infinity;
            points.forEach((candidate, index) => {
                const box = candidate.getBoundingClientRect();
                const delta = Math.hypot(event.clientX - box.x - box.width / 2, event.clientY - box.y - box.height / 2);
                if (delta < distance) { nearest = index; distance = delta; }
            });
            show(nearest);
        }
    };
    const key = event => {
        if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
        event.preventDefault();
        show(event.key === 'Home' ? 0 : event.key === 'End' ? Number.MAX_SAFE_INTEGER : selected < 0 ? 0 : selected + (event.key === 'ArrowRight' ? 1 : -1));
    };
    root.addEventListener('pointerover', pointer);
    root.addEventListener('pointerdown', pointer);
    root.addEventListener('keydown', key);
    // Keep axis text legible when an SVG scales down on narrow screens or at high text zoom.
    const observer = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(() => {
        const svg = root.querySelector('svg');
        if (!svg) return;
        const box = svg.getBoundingClientRect();
        const scale = Math.min(box.width / 480, box.height / 250);
        if (scale > 0) root.querySelectorAll('.telemetry-axis-label').forEach(label => {
            label.style.fontSize = `${12 / scale}px`;
        });
    });
    observer?.observe(root);
    bindings.set(root, { pointer, key, observer });
}
/** Removes all listeners when a chart is disposed. */
export function detach(root) {
    const binding = bindings.get(root);
    if (!binding) return;
    root.removeEventListener('pointerover', binding.pointer);
    root.removeEventListener('pointerdown', binding.pointer);
    root.removeEventListener('keydown', binding.key);
    binding.observer?.disconnect();
    bindings.delete(root);
}
