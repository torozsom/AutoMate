// Own reconnect controls for the mounted Blazor recovery dialog.
/** Mounted dialog receiving framework reconnect state events. */
const reconnectModal = document.getElementById("components-reconnect-modal");
reconnectModal.addEventListener("components-reconnect-state-changed", handleReconnectStateChanged);

/** User action for reconnecting the current circuit. */
const retryButton = document.getElementById("components-reconnect-button");
retryButton.addEventListener("click", retry);

/** User action for resuming a paused circuit. */
const resumeButton = document.getElementById("components-resume-button");
resumeButton.addEventListener("click", resume);

/**
 * Updates the modal for Blazor reconnect transitions and reloads rejected circuits.
 * @param {CustomEvent} event - Reconnect event with a state in its detail.
 * @returns {void} No value.
 */
function handleReconnectStateChanged(event) {
    if (event.detail.state === "show") {
        reconnectModal.showModal();
    } else if (event.detail.state === "hide") {
        reconnectModal.close();
    } else if (event.detail.state === "failed") {
        document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
    } else if (event.detail.state === "rejected") {
        location.reload();
    }
}

/**
 * Attempts reconnect, then circuit resume; unreachable servers rearm a visibility retry.
 * @returns {Promise<void>} Resolves after the attempt; handled connection failures wait for visibility or
 *     another retry.
 */
async function retry() {
    document.removeEventListener("visibilitychange", retryWhenDocumentBecomesVisible);

    try {
        // Reconnect will asynchronously return:
        // - true to mean success
        // - false to mean we reached the server, but it rejected the connection (e.g., unknown circuit ID)
        // - exception to mean we didn't reach the server (this can be sync or async)
        const successful = await Blazor.reconnect();
        if (!successful) {
            // We have been able to reach the server, but the circuit is no longer available.
            // We'll reload the page so the user can continue using the app as quickly as possible.
            const resumeSuccessful = await Blazor.resumeCircuit();
            if (!resumeSuccessful) {
                location.reload();
            } else {
                reconnectModal.close();
            }
        }
    } catch (err) {
        // We got an exception, server is currently unavailable
        document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
    }
}

/**
 * Attempts circuit resume and shows fixed failure state when the attempt fails.
 * @returns {Promise<void>} Resolves after resume or reload is requested; connection failures are handled in the
 *     modal.
 */
async function resume() {
    try {
        const successful = await Blazor.resumeCircuit();
        if (!successful) {
            location.reload();
        }
    } catch {
        reconnectModal.classList.replace("components-reconnect-paused", "components-reconnect-resume-failed");
    }
}

/**
 * Retries reconnect only when this document becomes visible.
 * @returns {Promise<void>} Resolves after any visibility-triggered reconnect attempt.
 */
async function retryWhenDocumentBecomesVisible() {
    if (document.visibilityState === "visible") {
        await retry();
    }
}
