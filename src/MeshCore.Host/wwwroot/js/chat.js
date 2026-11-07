// Live chat thread: appends messages pushed over SignalR (via live.js), updates delivery ticks in place,
// sends without a page reload, catches up after reconnects, and marks what you've seen as read.
// Without JavaScript the same page still works with a normal form post.
(function () {
    'use strict';

    var chat = document.getElementById('chat');
    var composer = document.getElementById('composer');
    var template = document.getElementById('bubble-template');
    if (!chat || !template) { return; }

    var key = chat.dataset.conversation;
    var me = chat.dataset.me;
    var isChannel = chat.dataset.channel === 'true';
    var token = composer ? composer.querySelector('input[name="__RequestVerificationToken"]') : null;
    var end = document.getElementById('chat-end');
    var senderContacts = {};
    try { senderContacts = JSON.parse(chat.dataset.senderContacts || '{}'); } catch (e) { senderContacts = {}; }

    var icons = {
        Sending: 'bi-clock', Sent: 'bi-check2', Delivered: 'bi-check2-all', NotConfirmed: 'bi-question-circle', Failed: 'bi-exclamation-circle',
    };

    function conversationKey(m) {
        return m.kind === 'Channel' ? 'c:' + m.channelIndex : 'd:' + m.contactKeyPrefix;
    }

    function newestId() {
        var ids = Array.prototype.map.call(chat.querySelectorAll('[data-id]'), function (b) { return parseInt(b.dataset.id, 10) || 0; });
        return ids.length ? Math.max.apply(null, ids) : 0;
    }

    function formatTime(iso) {
        var d = new Date(iso);
        return d.toDateString() === new Date().toDateString()
            ? d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
            : d.toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
    }

    function setStatus(icon, m) {
        icon.className = 'bi ' + (icons[m.status] || '');
        icon.dataset.status = m.status;
        var title = m.status + (m.roundTripMs ? ' (' + m.roundTripMs + ' ms)' : '') + (m.error ? ': ' + m.error : '');
        icon.title = title;
        icon.setAttribute('aria-label', m.status);
    }

    /** A channel sender's name: opens a private chat when they're a (uniquely named) contact, else the "how to reach them" page. */
    function senderLink(name) {
        var a = document.createElement('a');
        a.className = 'app-sender';
        a.textContent = name;
        var contactKey = Object.prototype.hasOwnProperty.call(senderContacts, name.trim()) ? senderContacts[name.trim()] : null;
        if (contactKey) {
            a.href = chat.dataset.directUrl.replace('__key__', encodeURIComponent(contactKey));
            a.title = 'Send ' + name + ' a private message';
            var icon = document.createElement('i');
            icon.className = 'bi bi-chat-dots ms-1';
            icon.setAttribute('aria-hidden', 'true');
            a.appendChild(icon);
        } else {
            var url = chat.dataset.senderUrl;
            a.href = url + (url.indexOf('?') < 0 ? '?' : '&') + 'name=' + encodeURIComponent(name.trim());
            a.classList.add('unknown');
            a.title = name + " isn't in your contacts yet. Tap to see how to message them privately.";
        }
        return a;
    }

    function render(m) {
        var mine = m.direction === 'Outgoing';
        var el = template.content.firstElementChild.cloneNode(true);
        el.classList.add(mine ? 'mine' : 'theirs');
        if (m.textType === 'CliData') { el.classList.add('font-monospace', 'small'); }
        el.dataset.id = m.id;
        el.querySelector('[data-text]').textContent = m.text;
        if (!mine && isChannel && m.senderName) {
            var s = el.querySelector('[data-sender]');
            s.appendChild(senderLink(m.senderName));
            s.classList.remove('d-none');
        }
        if (mine && m.sentByUserId && m.sentByUserId !== me) {
            var by = el.querySelector('[data-by]');
            by.textContent = 'by another user';
            by.classList.remove('d-none');
        }
        if (!mine) {
            var sig = el.querySelector('[data-signal]');
            sig.textContent = (m.hops != null ? m.hops + ' hop(s)' : 'direct') + (m.snr != null ? ' · ' + m.snr.toFixed(1) + ' dB' : '');
            sig.classList.remove('d-none');
        }
        var time = el.querySelector('time');
        time.setAttribute('datetime', m.receivedAt);
        time.textContent = formatTime(m.receivedAt);
        if (mine) {
            var icon = el.querySelector('[data-status]');
            icon.classList.remove('d-none');
            setStatus(icon, m);
        }
        return el;
    }

    function nearBottom() {
        return window.innerHeight + window.scrollY >= document.body.scrollHeight - 200;
    }

    /** Adds a message or updates the one already shown (e.g. its delivery tick). */
    function upsert(m) {
        if (conversationKey(m) !== key) { return false; }
        var existing = chat.querySelector('[data-id="' + m.id + '"]');
        if (existing) {
            var icon = existing.querySelector('[data-status]');
            if (icon && m.direction === 'Outgoing') { setStatus(icon, m); }
            return false;
        }
        var stick = nearBottom();
        chat.insertBefore(render(m), end);
        var empty = chat.querySelector('[data-empty]');
        if (empty) { empty.classList.add('d-none'); }
        if (stick || m.direction === 'Outgoing') { end.scrollIntoView({ block: 'end', behavior: 'smooth' }); }
        return m.direction === 'Incoming';
    }

    // ---- Read markers ------------------------------------------------------------------

    function markRead() {
        if (document.visibilityState !== 'visible' || !token) { return; }
        var id = newestId();
        if (!id) { return; }


        var body = new URLSearchParams({ key: key, messageId: String(id), __RequestVerificationToken: token.value });
        fetch(chat.dataset.readUrl, { method: 'POST', body: body, credentials: 'same-origin', headers: { 'X-Requested-With': 'fetch' } })
            .then(function () { document.dispatchEvent(new CustomEvent('mesh:read')); });
    }

    document.addEventListener('mesh:message', function (e) {
        var m = e.detail;
        // Seen right away if the tab is visible; otherwise marked read when it becomes visible again.
        if (upsert(m) && m.status === 'None') {
            markRead();
        }
    });

    document.addEventListener('visibilitychange', function () {
        if (document.visibilityState === 'visible') { markRead(); }
    });

    // ---- Catch up after the live connection was down ---------------------------------------
    document.addEventListener('mesh:reconnected', function () {
        fetch(chat.dataset.updatesUrl + '?key=' + encodeURIComponent(key) + '&after=' + newestId(),
            { credentials: 'same-origin', headers: { 'X-Requested-With': 'fetch' } })
            .then(function (r) { return r.ok ? r.json() : []; })
            .then(function (messages) {
                var any = false;
                messages.forEach(function (m) { any = upsert(m) || any; });
                if (any) { markRead(); }
            });
    });

    // ---- Send without leaving the page --------------------------------------------------------
    if (composer) {
        var textarea = composer.querySelector('textarea');
        var button = composer.querySelector('[type=submit]');
        var error = composer.querySelector('[data-send-error]');

        setUpImages(composer, textarea, error);

        composer.addEventListener('submit', function (e) {
            e.preventDefault();
            if (button.disabled) { return; }
            button.disabled = true;
            error.classList.add('d-none');
            fetch(composer.action, {
                method: 'POST',
                body: new URLSearchParams(new FormData(composer)),
                credentials: 'same-origin',
                headers: { 'X-Requested-With': 'fetch' },
            }).then(function (r) {
                return r.json().then(function (data) { return { ok: r.ok, data: data }; });
            }).then(function (result) {
                if (!result.ok) { throw new Error(result.data.error || 'Sending failed.'); }
                upsert(result.data);
                textarea.value = '';
                textarea.dispatchEvent(new Event('input'));
            }).catch(function (err) {
                error.textContent = err.message || 'Sending failed.';
                error.classList.remove('d-none');
            }).finally(function () {
                textarea.dispatchEvent(new Event('input')); // re-evaluates the button state
                textarea.focus();
            });
        });
    }

    // ---- Photos: upload, then put the short link into the message box -----------------------
    function setUpImages(form, textarea, error) {
        var button = form.querySelector('[data-image-button]');
        var input = form.querySelector('[data-image-input]');
        var expiry = form.querySelector('[data-image-expiry]');
        var options = form.querySelector('[data-image-options]');
        var hint = form.querySelector('[data-composer-hint]');
        if (!button || !input || !form.dataset.uploadUrl) { return; }
        var maxBytes = parseInt(form.dataset.maxUpload, 10) || 0;
        var storageKey = 'mesh.imageExpiry';

        button.classList.remove('d-none');
        if (options) { options.classList.remove('d-none'); options.classList.add('d-inline-flex'); }
        try {
            var saved = window.localStorage.getItem(storageKey);
            if (saved && expiry && expiry.querySelector('option[value="' + saved + '"]')) { expiry.value = saved; }
        } catch (e) { /* storage blocked: keep the default */ }
        if (expiry) {
            expiry.addEventListener('change', function () {
                try { window.localStorage.setItem(storageKey, expiry.value); } catch (e) { /* ignore */ }
            });
        }

        button.addEventListener('click', function () { input.click(); });
        input.addEventListener('change', function () {
            if (input.files && input.files[0]) { upload(input.files[0]); }
            input.value = '';
        });
        // Paste a screenshot or copied image straight into the message box.
        textarea.addEventListener('paste', function (e) {
            var items = (e.clipboardData && e.clipboardData.items) || [];
            for (var i = 0; i < items.length; i++) {
                if (items[i].kind === 'file' && items[i].type.indexOf('image/') === 0) {
                    e.preventDefault();
                    upload(items[i].getAsFile());
                    return;
                }
            }
        });

        function insertLink(url) {
            var start = textarea.selectionStart != null ? textarea.selectionStart : textarea.value.length;
            var end = textarea.selectionEnd != null ? textarea.selectionEnd : start;
            var before = textarea.value.slice(0, start);
            var after = textarea.value.slice(end);
            var text = (before && !/\s$/.test(before) ? ' ' : '') + url + (after && !/^\s/.test(after) ? ' ' : '');
            textarea.value = before + text + after;
            var caret = before.length + text.length;
            textarea.focus();
            textarea.setSelectionRange(caret, caret);
            textarea.dispatchEvent(new Event('input'));
        }

        function upload(file) {
            if (!file || button.disabled) { return; }
            error.classList.add('d-none');
            if (maxBytes && file.size > maxBytes) {
                showError('That image is ' + (file.size / 1048576).toFixed(1) + ' MB; the limit is ' + Math.round(maxBytes / 1048576) + ' MB.');
                return;
            }
            var hintText = hint ? hint.textContent : '';
            var icon = button.querySelector('i');
            button.disabled = true;
            icon.className = 'spinner-border spinner-border-sm';
            if (hint) { hint.textContent = 'Uploading photo… (shrinking it and removing location data)'; }

            var body = new FormData();
            body.append('file', file, file.name || 'pasted.png');
            if (expiry) { body.append('expires', expiry.value); }
            if (token) { body.append('__RequestVerificationToken', token.value); }
            fetch(form.dataset.uploadUrl, { method: 'POST', body: body, credentials: 'same-origin', headers: { 'X-Requested-With': 'fetch' } })
                .then(function (r) {
                    return r.json().catch(function () { return {}; }).then(function (data) { return { ok: r.ok, data: data }; });
                })
                .then(function (result) {
                    if (!result.ok || !result.data.url) { throw new Error(result.data.error || 'The upload failed.'); }
                    insertLink(result.data.url);
                })
                .catch(function (err) { showError(err.message || 'The upload failed.'); })
                .finally(function () {
                    button.disabled = false;
                    icon.className = 'bi bi-image';
                    if (hint) { hint.textContent = hintText; }
                });
        }

        function showError(message) {
            error.textContent = message;
            error.classList.remove('d-none');
        }
    }
})();
