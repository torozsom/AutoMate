window.setTheme = (theme) => {
    document.documentElement.setAttribute('data-bs-theme', theme);
    localStorage.setItem('theme', theme);
}

window.getTheme = () => {
    return localStorage.getItem('theme') || 'light';
}

window.initializeTheme = () => {
    const theme = window.getTheme();
    document.documentElement.setAttribute('data-bs-theme', theme);
}
