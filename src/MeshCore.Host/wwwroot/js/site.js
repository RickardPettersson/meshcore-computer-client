// Small progressive enhancements. Every page works without JavaScript; this only makes it nicer.
(function () {
    'use strict';

    // ---- Light/dark toggle -------------------------------------------------------
    document.querySelectorAll('[data-theme-toggle]').forEach(function (button) {
        button.addEventListener('click', function () {
            var next = document.documentElement.getAttribute('data-bs-theme') === 'dark' ? 'light' : 'dark';
            document.documentElement.setAttribute('data-bs-theme', next);
            try { localStorage.setItem('meshcore-theme', next); } catch (e) { /* storage blocked */ }
        });
    });

    // ---- Confirm before destructive actions: <form data-confirm="Delete X?"> --------
    var modalEl = document.getElementById('confirm-modal');
    if (modalEl && window.bootstrap) {
        var modal = new bootstrap.Modal(modalEl);
        var pending = null;
        document.addEventListener('submit', function (e) {
            var form = e.target;
            if (!(form instanceof HTMLFormElement) || !form.dataset.confirm || form.dataset.confirmed === 'yes') {
                return;
            }
            e.preventDefault();
            pending = form;
            document.getElementById('confirm-body').textContent = form.dataset.confirm;
            document.getElementById('confirm-ok').textContent = form.dataset.confirmButton || 'Confirm';
            modal.show();
        });
        document.getElementById('confirm-ok').addEventListener('click', function () {
            if (pending) {
                pending.dataset.confirmed = 'yes';
                modal.hide();
                pending.requestSubmit ? pending.requestSubmit() : pending.submit();
            }
        });
    }

    // ---- Message composer: live byte counter (MeshCore limits text by UTF-8 bytes) ---
    document.querySelectorAll('[data-byte-limit]').forEach(function (input) {
        var limit = parseInt(input.dataset.byteLimit, 10);
        var counter = document.getElementById(input.dataset.counter);
        var submit = input.form && input.form.querySelector('[type=submit]');
        var encoder = new TextEncoder();
        function update() {
            var used = encoder.encode(input.value).length;
            if (counter) {
                counter.textContent = used + ' / ' + limit;
                counter.classList.toggle('text-danger', used > limit);
            }
            if (submit) { submit.disabled = used === 0 || used > limit; }
        }
        input.addEventListener('input', update);
        // Enter sends, Shift+Enter makes a new line (desktop habit).
        input.addEventListener('keydown', function (e) {
            if (e.key === 'Enter' && !e.shiftKey && !e.isComposing && submit && !submit.disabled) {
                e.preventDefault();
                input.form.requestSubmit ? input.form.requestSubmit() : input.form.submit();
            }
        });
        update();
    });

    // ---- Chat: start at the newest message ------------------------------------------
    var end = document.getElementById('chat-end');
    if (end) { end.scrollIntoView({ block: 'end' }); }

    // ---- Copy-to-clipboard buttons: <button data-copy="text"> ---------------------------
    document.querySelectorAll('[data-copy]').forEach(function (button) {
        button.addEventListener('click', function () {
            navigator.clipboard.writeText(button.dataset.copy).then(function () {
                var original = button.innerHTML;
                button.innerHTML = '<i class="bi bi-check2" aria-hidden="true"></i> Copied';
                setTimeout(function () { button.innerHTML = original; }, 1500);
            });
        });
    });

    // ---- Times: rendered in UTC on the server, shown in the viewer's own time zone ----
    // Again whenever live.js swaps in fresh server HTML (the chat list), or those times stay in UTC.
    function localizeTimes() {
        var today = new Date().toDateString();
        document.querySelectorAll('time[data-local-time]').forEach(function (el) {
            var d = new Date(el.getAttribute('datetime'));
            if (isNaN(d)) { return; }
            el.textContent = d.toDateString() === today
                ? d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
                : d.toLocaleString([], { year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
        });
    }
    localizeTimes();
    document.addEventListener('mesh:content', localizeTimes);

    // ---- Installable app (PWA). Browsers only allow service workers on HTTPS or localhost. ----
    if ('serviceWorker' in navigator && window.isSecureContext) {
        navigator.serviceWorker.register('/sw.js').catch(function () { /* not essential */ });
    }

    // ---- "Use this device's location" (node settings). Browsers allow it on HTTPS or localhost. ----
    document.querySelectorAll('[data-geolocate]').forEach(function (button) {
        if (!('geolocation' in navigator) || !window.isSecureContext) { button.disabled = true; button.title = 'Needs HTTPS'; return; }
        button.addEventListener('click', function () {
            navigator.geolocation.getCurrentPosition(function (pos) {
                document.getElementById(button.dataset.lat).value = pos.coords.latitude.toFixed(6);
                document.getElementById(button.dataset.lon).value = pos.coords.longitude.toFixed(6);
            }, function (err) { alert('Could not get the location: ' + err.message); }, { enableHighAccuracy: true, timeout: 10000 });
        });
    });

    // ---- "Loading…" screen while the next page is on its way -------------------------
    // Radio actions (login, status, telemetry, saving settings) can take many seconds. The screen appears when a
    // form is sent or a link is followed and the answer takes more than a moment; quick pages never show it.
    // Opt out with data-no-loading (downloads, which don't leave the page).
    var loading = document.getElementById('app-loading');
    if (loading) {
        var showTimer = null, giveUpTimer = null;
        var hideLoading = function () {
            clearTimeout(showTimer);
            clearTimeout(giveUpTimer);
            loading.hidden = true;
        };
        var showLoadingSoon = function () {
            hideLoading();
            showTimer = setTimeout(function () { loading.hidden = false; }, 300);
            giveUpTimer = setTimeout(hideLoading, 120000); // never trap the user if the page didn't change after all
        };
        // Registered after the confirm dialog and the page's own handlers, so anything they cancelled is skipped.
        document.addEventListener('submit', function (e) {
            var form = e.target;
            if (e.defaultPrevented || !(form instanceof HTMLFormElement) || form.target === '_blank'
                || form.hasAttribute('data-no-loading') || (e.submitter && e.submitter.hasAttribute('data-no-loading'))) {
                return;
            }
            showLoadingSoon();
        });
        document.addEventListener('click', function (e) {
            var link = e.target instanceof Element ? e.target.closest('a[href]') : null;
            if (!link || e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey
                || link.target === '_blank' || link.hasAttribute('download') || link.hasAttribute('data-no-loading')
                || link.hasAttribute('data-bs-toggle') || link.origin !== location.origin
                || (link.pathname === location.pathname && link.search === location.search && link.hash)) {
                return;
            }
            showLoadingSoon();
        });
        // Coming back with the browser's Back button restores the old page as it was: make sure it isn't covered.
        window.addEventListener('pageshow', hideLoading);
        document.addEventListener('keydown', function (e) { if (e.key === 'Escape') { hideLoading(); } });
    }
})();
