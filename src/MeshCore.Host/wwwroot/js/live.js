// Live updates on every signed-in page: one SignalR connection to /hubs/mesh (cookie-authenticated).
// Keeps the unread badges, the tab title, the radio status pill and the chat list current, and shows a toast
// for new messages. Chat pages listen for the 'mesh:message' event this file dispatches (see chat.js).
(function () {
    'use strict';

    var hubUrl = document.body.dataset.hub;
    if (!hubUrl || !window.signalR) { return; }

    var baseTitle = document.title.replace(/^\(\d+\+?\)\s*/, '');
    var currentConversation = (document.getElementById('chat') || {}).dataset?.conversation || null;

    function conversationKey(m) {
        return m.kind === 'Channel' ? 'c:' + m.channelIndex : 'd:' + m.contactKeyPrefix;
    }

    // ---- Unread badges and title ----------------------------------------------------
    function unreadTotal() {
        var el = document.querySelector('[data-unread-total]');
        return el ? parseInt(el.dataset.unreadTotal, 10) || 0 : 0;
    }

    function setUnreadTotal(n) {
        document.querySelectorAll('[data-unread-total]').forEach(function (el) {
            el.dataset.unreadTotal = String(n);
            el.textContent = n > 99 ? '99+' : String(n);
            el.setAttribute('aria-label', n + ' unread');
            el.classList.toggle('d-none', n === 0);
        });
        document.title = (n > 0 ? '(' + (n > 99 ? '99+' : n) + ') ' : '') + baseTitle;
    }

    setUnreadTotal(unreadTotal());

    // ---- Toasts -------------------------------------------------------------------------
    function toast(m) {
        var container = document.getElementById('toasts');
        if (!container || !window.bootstrap) { return; }
        var href = m.kind === 'Channel' ? '/Messages/Channel/' + m.channelIndex : '/Messages/Direct/' + m.contactKeyPrefix;
        var el = document.createElement('div');
        el.className = 'toast';
        el.setAttribute('role', 'status');
        var body = document.createElement('div');
        body.className = 'toast-body d-flex gap-2 position-relative';
        var link = document.createElement('a');
        link.className = 'stretched-link';
        link.href = href;
        link.setAttribute('aria-label', 'Open chat');
        body.appendChild(link);
        var icon = document.createElement('i');
        icon.className = 'bi ' + (m.kind === 'Channel' ? 'bi-hash' : 'bi-person') + ' text-primary';
        var text = document.createElement('div');
        text.className = 'text-truncate';
        var who = document.createElement('strong');
        who.textContent = (m.senderName || m.contactKeyPrefix || 'Someone') + (m.kind === 'Channel' ? ' in #' + m.channelIndex : '');
        text.appendChild(who);
        text.appendChild(document.createElement('br'));
        text.appendChild(document.createTextNode(m.text));
        body.appendChild(icon);
        body.appendChild(text);
        el.appendChild(body);
        container.appendChild(el);
        var t = new bootstrap.Toast(el, { delay: 6000 });
        el.addEventListener('hidden.bs.toast', function () { el.remove(); });
        t.show();
    }

    // ---- Radio status pill --------------------------------------------------------------
    var states = {
        Connected: ['text-bg-success', 'bi-wifi'],
        Connecting: ['text-bg-warning', 'bi-arrow-repeat'],
        InUseElsewhere: ['text-bg-warning', 'bi-person-lock'],
        WaitingToRetry: ['text-bg-danger', 'bi-wifi-off'],
    };
    var labels = { Connecting: 'Connecting…', InUseElsewhere: 'In use elsewhere', WaitingToRetry: 'Offline' };

    function showRadio(device) {
        var style = states[device.state] || ['text-bg-secondary', 'bi-question-circle'];
        var label = device.state === 'Connected' ? (device.node && device.node.name) || 'Connected' : labels[device.state] || device.state;
        document.querySelectorAll('.app-status').forEach(function (pill) {
            pill.className = pill.className.replace(/text-bg-\w+/g, '').trim() + ' ' + style[0];
            var i = pill.querySelector('i');
            if (i) { i.className = 'bi ' + style[1]; }
            var span = pill.querySelector('span');
            if (span) { span.textContent = label; }
            pill.title = device.lastError || device.endpoint;
        });
    }

    // ---- Chat list page: swap in a fresh list when something changes ---------------------
    var liveList = document.querySelector('[data-live-list]');
    var listTimer = null;
    function refreshList() {
        if (!liveList) { return; }
        clearTimeout(listTimer);
        listTimer = setTimeout(function () {
            fetch(liveList.dataset.liveList, { headers: { 'X-Requested-With': 'fetch' }, credentials: 'same-origin' })
                .then(function (r) { return r.ok ? r.text() : null; })
                .then(function (html) {
                    if (html) {
                        liveList.innerHTML = html;
                        document.dispatchEvent(new CustomEvent('mesh:content'));
                    }
                });
        }, 300);
    }

    // ---- Connection -------------------------------------------------------------------------
    var connection = new signalR.HubConnectionBuilder()
        .withUrl(hubUrl)
        .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
        .configureLogging(signalR.LogLevel.Warning)
        .build();

    // The badge always shows the server's count (per user, read markers included), fetched shortly after changes.
    var countUrl = document.body.dataset.unreadUrl;
    var countTimer = null;
    function refreshUnread() {
        if (!countUrl) { return; }
        clearTimeout(countTimer);
        countTimer = setTimeout(function () {
            fetch(countUrl, { headers: { 'X-Requested-With': 'fetch' }, credentials: 'same-origin' })
                .then(function (r) { return r.ok ? r.json() : null; })
                .then(function (data) { if (data) { setUnreadTotal(data.total); } });
        }, 700); // after chat.js has had a chance to mark an open conversation read
    }

    var toasted = new Set();
    connection.on('message', function (m) {
        var key = conversationKey(m);
        var viewing = key === currentConversation && document.visibilityState === 'visible';
        document.dispatchEvent(new CustomEvent('mesh:message', { detail: m }));

        if (m.direction === 'Incoming' && m.status === 'None' && !viewing && !toasted.has(m.id)) {
            toasted.add(m.id);
            toast(m);
        }
        refreshUnread();
        refreshList();
    });

    connection.on('radio', showRadio);

    function setLive(on) {
        document.querySelectorAll('[data-live-state]').forEach(function (el) { el.classList.toggle('d-none', !on); });
    }

    connection.onreconnecting(function () { setLive(false); });
    connection.onreconnected(function () {
        setLive(true);
        document.dispatchEvent(new CustomEvent('mesh:reconnected'));
        refreshList();
    });
    connection.onclose(function () { setLive(false); });

    function start() {
        connection.start().then(function () { setLive(true); }).catch(function () {
            // Signed out or server restarting: try again in a while (a reload would also do).
            setTimeout(start, 15000);
        });
    }
    start();

    // chat.js marked something read (e.g. the tab became visible again): refresh the count.
    document.addEventListener('mesh:read', refreshUnread);
    connection.onreconnected(refreshUnread);
})();
