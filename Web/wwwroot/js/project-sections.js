/** Installs scoped section navigation, including enhanced-navigation fragments and the terminal AI shortcut. */
export function attach(nav) {
    const root = nav.closest('.project-console');
    const links = [...nav.querySelectorAll('a[href^="#"]')];
    const sections = links.map(link => root.querySelector(link.hash)).filter(Boolean);
    let disposed = false;
    let frame;
    const offset = () => {
        const top = document.querySelector('.top-row');
        return (top && getComputedStyle(top).position === 'sticky' ? top.getBoundingClientRect().height : 0)
            + nav.getBoundingClientRect().height + 20;
    };

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

    function go(hash, update, animate) {
        const target = sections.find(section => '#' + section.id === hash);
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

    function click(event) {
        const link = event.target.closest('a[href^="#"]');
        if (!link || !root.contains(link) || event.button !== 0 || event.ctrlKey || event.metaKey || event.altKey || event.shiftKey) return;
        if (!sections.some(section => '#' + section.id === link.hash)) return;
        event.preventDefault();
        go(link.hash, true, true);
    }

    const restore = () => go(location.hash, false, false);
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
    const initialRestore = setTimeout(() => {
        restore();
        mark();
    }, 0);
    return {
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
