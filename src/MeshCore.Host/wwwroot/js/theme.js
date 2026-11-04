// Runs in <head> before the page paints, so there's no flash of the wrong theme.
// Follows the system light/dark setting unless the user picked one with the toggle.
(function () {
    var stored = null;
    try { stored = localStorage.getItem('meshcore-theme'); } catch (e) { /* storage blocked */ }
    var dark = stored ? stored === 'dark' : window.matchMedia('(prefers-color-scheme: dark)').matches;
    document.documentElement.setAttribute('data-bs-theme', dark ? 'dark' : 'light');
})();
