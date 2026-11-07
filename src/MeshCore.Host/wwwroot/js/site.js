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
})();
