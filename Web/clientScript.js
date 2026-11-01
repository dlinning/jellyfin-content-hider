(function () {
    'use strict';

    if (window.__contentHiderInitialized) {
        return;
    }
    window.__contentHiderInitialized = true;

    /**
     * Makes an API call to the Jellyfin instance, authorizing
     * the current User
     *
     * @param {string} path
     * @param {RequestInit} options
     * @return {object|null} 
     */
    function api(path, options) {
        if (!window.ApiClient || typeof window.ApiClient.getUrl !== 'function') {
            return Promise.reject(new Error('ApiClient unavailable'));
        }
        
        const url = window.ApiClient.getUrl(path);
        const token = window.ApiClient.accessToken && window.ApiClient.accessToken();
        
        const headers = Object.assign(
            { 'Content-Type': 'application/json' },
            // Use the native MediaBrowser schema header instead of X-Emby-Token
            token ? { 'Authorization': `MediaBrowser Token="${token}"` } : {},
            (options && options.headers) || {}
        );
        
        return fetch(url, Object.assign({}, options, { headers })).then(function (res) {
            if (res.status === 204) return null;
            return res.ok ? res.json() : Promise.reject(new Error('HTTP ' + res.status));
        });
    }


    function updateHeaderButton(btn, status) {
        const active = status && (status.IsEffectivelyBlocking ?? status.isEffectivelyBlocking);
        const count = (status && (status.BlockedCount ?? status.blockedCount)) || 0;
        btn.title = active
            ? 'Content Hider: Active (' + count + ' hidden) — Click to toggle or manage'
            : 'Content Hider: Paused (Showing All Library Content) — Click to re-enable';
        btn.style.color = active ? '' : '#ffd166';
        btn.setAttribute('data-blocking-active', active ? 'true' : 'false');
    }

    function ensureHeaderButton() {
        const headerRight = document.querySelector('.headerRight');
        if (!headerRight || document.getElementById('chHeaderToggleBtn')) {
            return;
        }

        const btn = document.createElement('button');
        btn.id = 'chHeaderToggleBtn';
        btn.type = 'button';
        btn.className = 'paper-icon-button-light headerButton';
        btn.innerHTML = '<span class="material-icons visibility_off" aria-hidden="true"></span>';
        btn.title = 'Content Hider';

        btn.addEventListener('click', function (e) {
            e.preventDefault();
            if (e.shiftKey) {
                window.open(window.ApiClient.getUrl('ContentHider'), '_blank', 'noopener');
                return;
            }
            const currentlyActive = btn.getAttribute('data-blocking-active') !== 'false';
            api('ContentHider/Api/Toggle', {
                method: 'POST',
                body: JSON.stringify({ Enabled: !currentlyActive, DurationMinutes: currentlyActive ? 60 : null })
            }).then(function (status) {
                updateHeaderButton(btn, status);
                window.location.reload();
            });
        });

        headerRight.insertBefore(btn, headerRight.firstChild);
        api('ContentHider/Api/Status').then(function (status) {
            updateHeaderButton(btn, status);
        }).catch(function () {});
    }

    function ensureDetailButton() {
        const detailButtons = document.querySelector('.itemDetailPage:not(.hide) .mainDetailButtons');
        if (!detailButtons || detailButtons.querySelector('.chDetailHideBtn')) {
            return;
        }

        const params = new URLSearchParams(window.location.hash.split('?')[1] || '');
        const itemId = params.get('id');
        if (!itemId) {
            return;
        }

        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'button-flat detailButton button-flat btnMoreCommands detailButton emby-buttonn';
        btn.title = 'Hide Content for Me';
        btn.innerHTML = '<div class="detailButton-content"><span class="material-icons visibility_off detailButton-icon" aria-hidden="true"></span></div>';

        btn.addEventListener('click', function () {
            api('ContentHider/Api/Blocks/' + encodeURIComponent(itemId), { method: 'POST' })
                .then(function () {
                    if (window.Emby && window.Emby.Page && typeof window.Emby.Page.back === 'function') {
                        window.Emby.Page.back();
                    } else {
                        window.history.back();
                    }
                });
        });

        detailButtons.appendChild(btn);
    }

    const observer = new MutationObserver(function () {
        ensureHeaderButton();
        ensureDetailButton();
    });

    observer.observe(document.documentElement, { childList: true, subtree: true });
})();
