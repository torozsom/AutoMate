// Local interactions contain no application data, provider access or network requests.
export function isVisible() {
    return document.visibilityState === "visible";
}

export function copy(value) {
    return navigator.clipboard.writeText(value);
}

export function attachDialog(root, callback) {
    const previous = document.activeElement;
    const focusable = () => [...root.querySelectorAll('button:not([disabled]),a[href],input:not([disabled]),select:not([disabled]),textarea:not([disabled]),[tabindex="0"]')].filter(e => e.getClientRects().length);
    const key = e => {
        if (e.key === "Escape") {
            e.preventDefault();
            callback.invokeMethodAsync("CloseDialog");
        }
        if (e.key === "Tab") {
            const items = focusable();
            if (!items.length) {
                e.preventDefault();
                root.focus();
                return;
            }
            const first = items[0], last = items[items.length - 1];
            if (e.shiftKey && (document.activeElement === first || document.activeElement === root)) {
                e.preventDefault();
                last.focus();
            } else if (!e.shiftKey && document.activeElement === last) {
                e.preventDefault();
                first.focus();
            }
        }
    };
    root.addEventListener("keydown", key);
    (focusable()[0] || root).focus();
    return {
        dispose() {
            root.removeEventListener("keydown", key);
            if (previous?.isConnected) previous.focus();
        }
    };
}
