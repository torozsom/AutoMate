/**
 * Installs scoped section navigation and scroll tracking; the caller owns disposal.
 * @param {HTMLElement} nav - Navigation within a mounted project console.
 * @returns {object} Registration whose dispose method cancels scheduled work and removes listeners.
 */
export function attach(nav) {
    const root = nav.closest('.project-console');
    const links = [...nav.querySelectorAll('a[href^="#"]')];
    const sections = links.map(
        /**
         * Resolves a navigation fragment within this project console.
         * @param {HTMLAnchorElement} link - Section navigation link.
         * @returns {Element|null} Matching section, or null when absent.
         */
        link => root.querySelector(link.hash)).filter(Boolean);
    let disposed = false;
    let frame;
    /**
     * Measures the sticky shell and navigation clearance above a target section.
     * @returns {number} Vertical clearance in CSS pixels, including a 20-pixel margin.
     */
    const offset = () => {
        const top = document.querySelector('.top-row');
        return (top && getComputedStyle(top).position === 'sticky' ? top.getBoundingClientRect().height : 0)
            + nav.getBoundingClientRect().height + 20;
    };

    /**
     * Updates the active link and aria-current from the current section positions.
     * @returns {void} No value; disposed registrations do nothing.
     */
    function mark() {
        if (disposed) return;
        let active = sections[0];
        for (const section of sections) if (section.getBoundingClientRect().top <= offset() + 24) active = section;
        if (window.scrollY > 0 && window.scrollY + window.innerHeight >= document.documentElement.scrollHeight - 2)
            active = sections.at(-1);
        for (const link of links) {
            const selected = link.hash === '#' + active?.id;
            link.classList.toggle('active', selected);
            if (selected) link.setAttribute('aria-current', 'location');
            else link.removeAttribute('aria-current');
        }
    }

    /**
     * Focuses and scrolls to a known section, honoring reduced-motion preference.
     * @param {string} hash - Section fragment to resolve.
     * @param {boolean} update - Whether to push a changed fragment into browser history.
     * @param {boolean} animate - Whether smooth scrolling is requested.
     * @returns {void} No value; unknown sections and disposed registrations do nothing.
     */
    function go(hash, update, animate) {
        const target = sections.find(
            /**
             * Matches the section against the requested fragment.
             * @param {HTMLElement} section - Known project section.
             * @returns {boolean} True when its ID matches the fragment.
             */
            section => '#' + section.id === hash);
        if (!target || disposed) return;
        if (update && location.hash !== hash) history.pushState(null, '', location.pathname + location.search + hash);
        const heading = target.querySelector('h2') ?? target;
        heading.setAttribute('tabindex', '-1');
        heading.focus({preventScroll: true});
        window.scrollTo({
            top: Math.max(0, target.getBoundingClientRect().top + window.scrollY - offset()),
            behavior: animate && !matchMedia('(prefers-reduced-motion: reduce)').matches ? 'smooth' : 'instant'
        });
        mark();
    }

    /**
     * Intercepts unmodified primary clicks on known project section links.
     * @param {MouseEvent} event - Delegated click event.
     * @returns {void} No value; unrelated clicks retain native browser behavior.
     */
    function click(event) {
        const link = event.target.closest('a[href^="#"]');
        if (!link || !root.contains(link) || event.button !== 0 || event.ctrlKey || event.metaKey || event.altKey || event.shiftKey) return;
        if (!sections.some(
            /**
             * Checks whether the clicked fragment names a known section.
             * @param {HTMLElement} section - Known project section.
             * @returns {boolean} True when the section matches the clicked link.
             */
            section => '#' + section.id === link.hash)) return;
        event.preventDefault();
        go(link.hash, true, true);
    }

    /**
     * Restores the current fragment without adding history or animation.
     * @returns {void} No value.
     */
    const restore = () => go(location.hash, false, false);
    /**
     * Coalesces scroll and resize updates into one animation frame.
     * @returns {void} No value.
     */
    const scroll = () => {
        cancelAnimationFrame(frame);
        frame = requestAnimationFrame(mark);
    };
    root.addEventListener('click', click);
    window.addEventListener('popstate', restore);
    window.addEventListener('hashchange', restore);
    window.addEventListener('scroll', scroll, {passive: true});
    window.addEventListener('resize', scroll);
    // Native fragment scrolling can fire before the first animation frame and cancel a pending scroll update.
    restore();
    mark();
    const initialRestore = setTimeout(
        /**
         * Repeats initial fragment restoration after native browser scrolling settles.
         * @returns {void} No value.
         */
        () => {
            restore();
            mark();
        }, 0);
    return {
        /**
         * Cancels pending navigation updates and detaches all scoped listeners.
         * @returns {void} No value.
         */
        dispose() {
            disposed = true;
            cancelAnimationFrame(frame);
            clearTimeout(initialRestore);
            root.removeEventListener('click', click);
            window.removeEventListener('popstate', restore);
            window.removeEventListener('hashchange', restore);
            window.removeEventListener('scroll', scroll);
            window.removeEventListener('resize', scroll);
        }
    };
}
