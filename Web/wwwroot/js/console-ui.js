// Local interactions contain no application data, provider access or network requests.
/**
 * Reports whether this browser document is visible.
 * @returns {boolean} True when the document is visible.
 */
export function isVisible() {
    return document.visibilityState === "visible";
}

/**
 * Copies explicit user-selected text through the browser clipboard API.
 * @param {string} value - Text to copy.
 * @returns {Promise<void>} Resolves after writing the clipboard; rejects if permission or browser policy denies
 *     access.
 * @throws {DOMException} Clipboard access is unavailable or denied.
 */
export function copy(value) {
    return navigator.clipboard.writeText(value);
}

/**
 * Attaches an Escape handler and focus trap; the caller must dispose the registration.
 * @param {HTMLElement} root - Mounted dialog root.
 * @param {object} callback - Blazor object reference exposing CloseDialog.
 * @returns {object} Registration whose dispose method removes listeners and restores connected prior focus.
 */
export function attachDialog(root, callback) {
    const previous = document.activeElement;
    /**
     * Finds enabled, visible focus targets inside the dialog.
     * @returns {HTMLElement[]} Visible focusable elements in DOM order.
     */
    const focusable = () => [...root.querySelectorAll('button:not([disabled]),a[href],input:not([disabled]),select:not([disabled]),textarea:not([disabled]),[tabindex="0"]')].filter(
        /**
         * Includes only elements with a rendered layout box.
         * @param {HTMLElement} e - Candidate focus target.
         * @returns {number} Number of client rectangles; zero excludes a hidden element.
         */
        e => e.getClientRects().length);
    /**
     * Handles Escape and keeps Tab traversal within the dialog.
     * @param {KeyboardEvent} e - Dialog key event.
     * @returns {void} No value; Escape requests the Blazor close callback.
     */
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
        /**
         * Detaches dialog keyboard handling and restores previous focus when still connected.
         * @returns {void} No value.
         */
        dispose() {
            root.removeEventListener("keydown", key);
            if (previous?.isConnected) previous.focus();
        }
    };
}
