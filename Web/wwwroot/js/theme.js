window.setTheme = (theme) => {
    document.documentElement.setAttribute('data-bs-theme', theme);
    localStorage.setItem('theme', theme);
}

window.getTheme = () => {
    const theme = localStorage.getItem('theme');
    return theme === 'light' || theme === 'dark' ? theme : 'dark';
}

window.initializeTheme = () => {
    const theme = window.getTheme();
    document.documentElement.setAttribute('data-bs-theme', theme);
}
