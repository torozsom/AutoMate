/**
 * Applies and saves the caller-selected browser theme.
 * @param {string} theme - Theme identifier, normally light or dark.
 * @returns {void} No value; the DOM is updated before local storage is written.
 * @throws {DOMException} Browser policy blocks local storage access.
 */
window.setTheme = (theme) => {
    document.documentElement.setAttribute('data-bs-theme', theme);
    localStorage.setItem('theme', theme);
}

/**
 * Reads the saved supported theme with a dark fallback.
 * @returns {string} Saved light/dark theme, or dark when absent or unsupported.
 * @throws {DOMException} Browser policy blocks local storage access.
 */
window.getTheme = () => {
    const theme = localStorage.getItem('theme');
    return theme === 'light' || theme === 'dark' ? theme : 'dark';
}

/**
 * Applies the saved theme during document startup to reduce theme flash.
 * @returns {void} No value.
 * @throws {DOMException} Browser policy blocks reading the saved theme.
 */
window.initializeTheme = () => {
    const theme = window.getTheme();
    document.documentElement.setAttribute('data-bs-theme', theme);
}
