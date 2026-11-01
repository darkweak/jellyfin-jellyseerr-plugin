/*
 * Jellyseerr Requester - Jellyfin web client integration.
 * Jellyseerr titles are added to search results by the server, so they work in every client
 * (requesting there = adding to favorites). In Jellyfin web, this script also adds a "Request"
 * button with a season picker to their detail pages, and Trending / Popular rows to the home page.
 * Works the same with Jellyseerr and Seerr (same API).
 */
(function () {
    'use strict';

    if (window.__jellyseerrRequesterLoaded) {
        return;
    }
    window.__jellyseerrRequesterLoaded = true;

    var POSTER_BASE = 'https://image.tmdb.org/t/p/w300';
    var BACKDROP_BASE = 'https://image.tmdb.org/t/p/w780';
    var BUTTON_ID = 'jsr-request-button';
    // Virtual item ids: 4a535252-KKKK-SSSS-5345-4552TTTTTTTT (K kind, S season, T TMDB id); see VirtualId.cs.
    var VIRTUAL_ID = /^4a535252([0-9a-f]{4})([0-9a-f]{4})53454552([0-9a-f]{8})$/i;

    var STATUS = { UNKNOWN: 1, PENDING: 2, PROCESSING: 3, PARTIAL: 4, AVAILABLE: 5, BLACKLISTED: 6, DELETED: 7 };
    // Same wording as the poster badges (PosterRenderer.cs).
    var STATUS_LABEL = {
        2: 'Requested',
        3: 'Downloading',
        4: 'Partly available',
        5: 'Available',
        6: 'Blocked'
    };
    // Label of the disabled Request button when the title can't be requested.
    var DONE_LABEL = {
        2: 'Already requested',
        3: 'Downloading',
        4: 'Partly available',
        5: 'Already available',
        6: 'Can\'t be requested'
    };

    var state = {
        config: null,
        configUserId: null,
        configPromise: null,
        requestable: {},
        discover: null,
        discoverAt: 0,
        discoverPromise: null
    };

    var DISCOVER_ROWS = [
        { list: 'trending', title: 'Trending' },
        { list: 'movies', title: 'Popular Movies' },
        { list: 'tv', title: 'Popular TV Shows' }
    ];
    var DISCOVER_TTL = 5 * 60 * 1000;

    // ---------------------------------------------------------------- utils

    function escapeHtml(value) {
        return String(value == null ? '' : value)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    // Jellyfin serializes PascalCase by default; normalize to camelCase.
    function camelize(value) {
        if (Array.isArray(value)) {
            return value.map(camelize);
        }
        if (value && typeof value === 'object') {
            var out = {};
            Object.keys(value).forEach(function (k) {
                out[k.charAt(0).toLowerCase() + k.slice(1)] = camelize(value[k]);
            });
            return out;
        }
        return value;
    }

    function icon(name) {
        // Jellyfin web ships Material Icons with one class per glyph.
        return '<span class="material-icons ' + name + '" aria-hidden="true"></span>';
    }

    function apiClient() {
        return window.ApiClient || null;
    }

    function currentUserId() {
        var client = apiClient();
        try {
            return client && client.getCurrentUserId ? client.getCurrentUserId() : null;
        } catch (e) {
            return null;
        }
    }

    function api(method, path, body) {
        var client = apiClient();
        if (!client || !client.accessToken || !client.accessToken()) {
            return Promise.reject(new Error('Not signed in'));
        }

        var options = {
            method: method,
            headers: {
                'Accept': 'application/json',
                'Authorization': 'MediaBrowser Token="' + client.accessToken() + '"'
            }
        };
        if (body !== undefined) {
            options.headers['Content-Type'] = 'application/json';
            options.body = JSON.stringify(body);
        }

        return fetch(client.getUrl('JellyseerrRequester/' + path), options).then(function (response) {
            return response.text().then(function (text) {
                var data = null;
                try {
                    data = text ? camelize(JSON.parse(text)) : null;
                } catch (e) {
                    data = null;
                }
                if (!response.ok) {
                    var err = new Error((data && data.message) || ('HTTP ' + response.status));
                    err.status = response.status;
                    throw err;
                }
                return data;
            });
        });
    }

    function getConfig() {
        var userId = currentUserId();
        if (state.config && state.configUserId === userId) {
            return Promise.resolve(state.config);
        }
        if (state.configPromise && state.configUserId === userId) {
            return state.configPromise;
        }

        state.configUserId = userId;
        state.configPromise = api('GET', 'Config').then(function (config) {
            state.config = config;
            state.configPromise = null;
            return config;
        }, function (err) {
            state.configPromise = null;
            throw err;
        });
        return state.configPromise;
    }

    // Jellyfin themes are mostly dark, but "Light" exists: match whichever is active.
    function isLightTheme() {
        var nodes = [document.querySelector('.backgroundContainer'), document.body, document.documentElement];
        for (var i = 0; i < nodes.length; i++) {
            if (!nodes[i]) {
                continue;
            }
            var m = /rgba?\(([^)]+)\)/.exec(getComputedStyle(nodes[i]).backgroundColor || '');
            var parts = m ? m[1].split(',').map(parseFloat) : null;
            if (parts && (parts.length < 4 || parts[3] > 0.5)) {
                return (0.299 * parts[0] + 0.587 * parts[1] + 0.114 * parts[2]) / 255 > 0.6;
            }
        }
        return false;
    }

    function uiClass() {
        return 'jsr-ui' + (isLightTheme() ? ' jsr-light' : '');
    }

    function toast(message, isError) {
        var el = document.createElement('div');
        el.className = uiClass() + ' jsr-toast' + (isError ? ' jsr-toast-error' : '');
        el.setAttribute('role', isError ? 'alert' : 'status');
        el.innerHTML = icon(isError ? 'error_outline' : 'check_circle') + '<span></span>';
        el.lastChild.textContent = message;
        document.body.appendChild(el);
        setTimeout(function () { el.classList.add('jsr-toast-show'); }, 10);
        setTimeout(function () {
            el.classList.remove('jsr-toast-show');
            setTimeout(function () { el.remove(); }, 300);
        }, 4000);
    }

    // ---------------------------------------------------------------- styles

    // Shape rule: containers (dialog, rows, notices, toast) are rounded, controls (buttons, pills) are full pills.
    function injectStyles() {
        if (document.getElementById('jsr-styles')) {
            return;
        }
        var css = [
            // Theme tokens. Indigo is Jellyseerr's brand color; status colors match the poster badges.
            '.jsr-ui{--jsr-surface:#1b1b21;--jsr-raised:rgba(255,255,255,.06);--jsr-raised-hover:rgba(255,255,255,.1);--jsr-border:rgba(255,255,255,.09);--jsr-text:#ececf1;--jsr-muted:rgba(236,236,241,.66);--jsr-accent:#4f46e5;--jsr-accent-hover:#6366f1;--jsr-accent-soft:rgba(99,102,241,.16);--jsr-accent-text:#a5b4fc;--jsr-danger:#fca5a5;--jsr-shadow:rgba(0,0,0,.5);',
            '--jsr-s2:#fbbf24;--jsr-s2b:rgba(217,119,6,.18);--jsr-s3:#93c5fd;--jsr-s3b:rgba(37,99,235,.2);--jsr-s4:#5eead4;--jsr-s4b:rgba(13,148,136,.2);--jsr-s5:#86efac;--jsr-s5b:rgba(22,163,74,.2);--jsr-s6:#fca5a5;--jsr-s6b:rgba(220,38,38,.2);}',
            '.jsr-ui.jsr-light{--jsr-surface:#fbfbfc;--jsr-raised:rgba(20,20,40,.05);--jsr-raised-hover:rgba(20,20,40,.09);--jsr-border:rgba(20,20,40,.1);--jsr-text:#18181f;--jsr-muted:rgba(24,24,31,.64);--jsr-accent-soft:rgba(79,70,229,.1);--jsr-accent-text:#4338ca;--jsr-danger:#b91c1c;--jsr-shadow:rgba(20,20,40,.22);',
            '--jsr-s2:#b45309;--jsr-s2b:rgba(217,119,6,.12);--jsr-s3:#1d4ed8;--jsr-s3b:rgba(37,99,235,.1);--jsr-s4:#0f766e;--jsr-s4b:rgba(13,148,136,.12);--jsr-s5:#15803d;--jsr-s5b:rgba(22,163,74,.12);--jsr-s6:#b91c1c;--jsr-s6b:rgba(220,38,38,.1);}',

            // Request button on detail pages: replaces Play, so it is the one filled button.
            '#' + BUTTON_ID + '{display:inline-flex;flex-direction:row;align-items:center;gap:.4em;min-height:2.5em;padding:.45em 1.3em .45em 1em;margin:0 .6em 0 0;border:0;border-radius:999px;background:#4f46e5;color:#fff;font:inherit;font-weight:600;cursor:pointer;transition:background .15s,transform .1s;}',
            '#' + BUTTON_ID + ':hover{background:#6366f1;}',
            '#' + BUTTON_ID + ':active{transform:scale(.97);}',
            '#' + BUTTON_ID + ':focus-visible{outline:2px solid #a5b4fc;outline-offset:2px;}',
            '#' + BUTTON_ID + ' .material-icons{font-size:1.35em;}',
            '#' + BUTTON_ID + '[aria-busy="true"]{opacity:.7;cursor:progress;}',
            '#' + BUTTON_ID + '.jsr-floating{position:fixed;right:1.5em;bottom:1.5em;z-index:1000;box-shadow:0 8px 24px rgba(0,0,0,.4);}',

            // Virtual items can't be played: hide play overlays on their cards and play/shuffle/played on their pages.
            '.card[data-id^="4a535252"] .cardOverlayButton,.card[data-id^="4a535252"] .cardOverlayFab-primary,.card[data-id^="4a535252"] .cardOverlayButton-hover{display:none!important;}',
            'body.jsr-virtual-details .mainDetailButtons .btnPlay,body.jsr-virtual-details .mainDetailButtons .btnReplay,body.jsr-virtual-details .mainDetailButtons .btnResume,body.jsr-virtual-details .mainDetailButtons .btnShuffle,body.jsr-virtual-details .mainDetailButtons .btnInstantMix,body.jsr-virtual-details .mainDetailButtons .btnPlaystate,body.jsr-virtual-details .mainDetailButtons .btnDownload{display:none!important;}',

            // Home page rows. Card size and text come from Jellyfin's own card classes.
            '.jsr-discover-head{display:flex;align-items:center;gap:.5em;}',
            '.jsr-discover-head .sectionTitle{margin-right:auto;}',
            '.jsr-discover-link{display:inline-flex;align-items:center;gap:.25em;font-size:.9em;opacity:.75;color:inherit;text-decoration:none;}',
            '.jsr-discover-link:hover{opacity:1;text-decoration:underline;}',
            '.jsr-discover-link .material-icons{font-size:1.1em;}',
            '.jsr-discover-nav{display:inline-flex;align-items:center;justify-content:center;width:2.2em;height:2.2em;padding:0;border:0;border-radius:999px;background:none;color:inherit;cursor:pointer;opacity:.75;}',
            '.jsr-discover-nav:hover{opacity:1;background:rgba(128,128,128,.2);}',
            '@media (hover:none){.jsr-discover-nav{display:none;}}',
            '.jsr-discover-track{display:flex;overflow-x:auto;overflow-y:hidden;scroll-snap-type:x proximity;scrollbar-width:none;padding-top:.6em;padding-bottom:.4em;}',
            '.jsr-discover-track::-webkit-scrollbar{display:none;}',
            '.jsr-discover-track>.card{flex:none;scroll-snap-align:start;color:inherit;text-decoration:none;}',
            '.jsr-discover-track .cardImageContainer{overflow:hidden;}',
            '.jsr-card-img{position:absolute;inset:0;width:100%;height:100%;object-fit:cover;}',
            '.jsr-card-flag{position:absolute;top:.4em;right:.4em;display:flex;align-items:center;justify-content:center;width:1.8em;height:1.8em;border-radius:999px;background:#16a34a;color:#fff;box-shadow:0 2px 6px rgba(0,0,0,.4);}',
            '.jsr-card-flag .material-icons{font-size:1.2em;}',
            '.jsr-discover-track>.card:focus-visible{outline:none;}',
            '.jsr-discover-track>.card:focus-visible .cardScalable{outline:3px solid #a5b4fc;outline-offset:2px;border-radius:.2em;}',

            // Dialog.
            '.jsr-overlay{position:fixed;inset:0;z-index:100000;background:rgba(8,8,12,.72);display:flex;align-items:center;justify-content:center;padding:16px;}',
            '.jsr-dialog{position:relative;display:flex;flex-direction:column;width:100%;max-width:38em;max-height:90vh;max-height:90dvh;overflow:hidden;background:var(--jsr-surface);color:var(--jsr-text);border-radius:16px;box-shadow:0 24px 64px var(--jsr-shadow);}',
            '.jsr-hero{position:absolute;top:0;left:0;right:0;height:11em;background:center 25%/cover no-repeat;opacity:.55;}',
            '.jsr-hero::after{content:"";position:absolute;inset:0;background:linear-gradient(to bottom,transparent 15%,var(--jsr-surface));}',
            '.jsr-light .jsr-hero{opacity:.35;}',
            '.jsr-close{position:absolute;top:.7em;right:.7em;z-index:2;display:flex;align-items:center;justify-content:center;width:2.4em;height:2.4em;padding:0;border:0;border-radius:999px;background:rgba(0,0,0,.4);color:#fff;cursor:pointer;}',
            '.jsr-close:hover{background:rgba(0,0,0,.6);}',
            '.jsr-head{position:relative;display:flex;align-items:flex-end;gap:1.1em;padding:1.6em 1.5em 0;}',
            '.jsr-has-hero .jsr-head{padding-top:5em;}',
            '.jsr-poster{flex:none;width:6.5em;aspect-ratio:2/3;object-fit:cover;border-radius:10px;box-shadow:0 8px 24px var(--jsr-shadow);background:var(--jsr-raised);}',
            '.jsr-head-text{min-width:0;flex:1;padding-right:2.5em;}',
            '.jsr-dialog h2{margin:0;font-size:1.45em;line-height:1.2;font-weight:700;}',
            '.jsr-meta{display:flex;flex-wrap:wrap;align-items:center;gap:.25em 1em;margin-top:.35em;color:var(--jsr-muted);font-size:.9em;}',
            '.jsr-meta .material-icons{font-size:1.15em;vertical-align:-.2em;margin-right:.15em;color:#fbbf24;}',
            '.jsr-status-slot:not(:empty){margin-top:.6em;}',
            '.jsr-scroll{flex:1 1 auto;min-height:0;overflow:auto;padding:1.2em 1.5em .4em;}',
            '.jsr-overview{margin:0;line-height:1.55;color:var(--jsr-muted);display:-webkit-box;-webkit-box-orient:vertical;-webkit-line-clamp:4;overflow:hidden;}',
            '.jsr-overview.jsr-open{display:block;}',
            '.jsr-text-btn{border:0;background:none;padding:.2em 0;font:inherit;font-size:.9em;font-weight:600;color:var(--jsr-accent-text);cursor:pointer;}',
            '.jsr-text-btn:hover{text-decoration:underline;}',
            '.jsr-body>*{margin-top:1.2em;}',
            '.jsr-field-label{font-weight:600;margin-bottom:.5em;}',
            '.jsr-list-head{display:flex;align-items:center;justify-content:space-between;margin-bottom:.4em;}',
            '.jsr-list{display:grid;gap:.25em;}',
            '.jsr-row{display:grid;grid-template-columns:1.4em minmax(0,1fr) auto auto;align-items:center;gap:.75em;padding:.6em .8em;border-radius:10px;cursor:pointer;transition:background .15s;}',
            '.jsr-row:hover{background:var(--jsr-raised);}',
            '.jsr-row.jsr-row-on{background:var(--jsr-accent-soft);}',
            '.jsr-row input{width:1.15em;height:1.15em;margin:0;accent-color:#6366f1;cursor:pointer;}',
            '.jsr-row-locked{cursor:default;}',
            '.jsr-row-locked:hover{background:none;}',
            '.jsr-row-locked .material-icons{font-size:1.2em;color:var(--jsr-muted);}',
            '.jsr-row-locked .jsr-row-name{color:var(--jsr-muted);}',
            '.jsr-row-name{overflow:hidden;text-overflow:ellipsis;white-space:nowrap;}',
            '.jsr-row-sub{color:var(--jsr-muted);font-size:.85em;white-space:nowrap;}',
            '.jsr-pill{display:inline-block;padding:.2em .7em;border-radius:999px;font-size:.78em;font-weight:600;white-space:nowrap;color:var(--jsr-c);background:var(--jsr-b);}',
            '.jsr-pill-2{--jsr-c:var(--jsr-s2);--jsr-b:var(--jsr-s2b);}',
            '.jsr-pill-3{--jsr-c:var(--jsr-s3);--jsr-b:var(--jsr-s3b);}',
            '.jsr-pill-4{--jsr-c:var(--jsr-s4);--jsr-b:var(--jsr-s4b);}',
            '.jsr-pill-5{--jsr-c:var(--jsr-s5);--jsr-b:var(--jsr-s5b);}',
            '.jsr-pill-6{--jsr-c:var(--jsr-s6);--jsr-b:var(--jsr-s6b);}',
            '.jsr-segment{display:inline-flex;padding:3px;border-radius:999px;background:var(--jsr-raised);}',
            '.jsr-segment button{border:0;border-radius:999px;padding:.4em 1.1em;background:none;color:var(--jsr-muted);font:inherit;font-weight:600;cursor:pointer;}',
            '.jsr-segment button[aria-checked="true"]{background:var(--jsr-surface);color:var(--jsr-text);box-shadow:0 1px 3px var(--jsr-shadow);}',
            '.jsr-notice,.jsr-error{display:flex;align-items:flex-start;gap:.6em;padding:.75em .9em;border-radius:10px;background:var(--jsr-raised);line-height:1.45;font-size:.92em;}',
            '.jsr-notice .material-icons,.jsr-error .material-icons{font-size:1.3em;flex:none;}',
            '.jsr-notice .material-icons{color:var(--jsr-accent-text);}',
            '.jsr-error{margin-top:1em;color:var(--jsr-danger);background:var(--jsr-s6b);}',
            '.jsr-error[hidden]{display:none;}',
            '.jsr-skel{height:2.6em;border-radius:10px;background:var(--jsr-raised);}',
            '.jsr-footer{display:flex;flex-wrap:wrap;align-items:center;justify-content:flex-end;gap:.75em 1em;padding:1em 1.5em;border-top:1px solid var(--jsr-border);}',
            '.jsr-link{display:inline-flex;align-items:center;gap:.3em;margin-right:auto;color:var(--jsr-accent-text);font-size:.9em;font-weight:600;text-decoration:none;}',
            '.jsr-link:hover{text-decoration:underline;}',
            '.jsr-link .material-icons{font-size:1.1em;}',
            '.jsr-btn{border:0;border-radius:999px;padding:.65em 1.4em;font:inherit;font-weight:600;white-space:nowrap;cursor:pointer;background:var(--jsr-raised);color:var(--jsr-text);transition:background .15s,transform .1s;}',
            '.jsr-btn:hover{background:var(--jsr-raised-hover);}',
            '.jsr-btn:active{transform:scale(.97);}',
            '.jsr-btn-primary{background:var(--jsr-accent);color:#fff;}',
            '.jsr-btn-primary:hover{background:var(--jsr-accent-hover);}',
            '.jsr-btn[disabled]{opacity:.5;cursor:default;transform:none;}',
            '.jsr-btn-primary[disabled]:hover{background:var(--jsr-accent);}',
            '.jsr-ui button:focus-visible,.jsr-ui a:focus-visible,.jsr-ui input:focus-visible{outline:2px solid var(--jsr-accent-text);outline-offset:2px;}',

            // Toast.
            '.jsr-toast{position:fixed;left:50%;bottom:2em;z-index:100001;display:flex;align-items:center;gap:.6em;max-width:min(32em,90vw);padding:.8em 1.2em .8em 1em;border-radius:12px;background:var(--jsr-surface);color:var(--jsr-text);box-shadow:0 8px 32px var(--jsr-shadow);opacity:0;transform:translate(-50%,1em);transition:opacity .25s,transform .25s;}',
            '.jsr-toast .material-icons{color:var(--jsr-s5);flex:none;}',
            '.jsr-toast-error .material-icons{color:var(--jsr-danger);}',
            '.jsr-toast-show{opacity:1;transform:translate(-50%,0);}',

            // Motion only acknowledges opening and loading; none of it when the user asks for less.
            '@media (prefers-reduced-motion:no-preference){',
            '.jsr-overlay{animation:jsr-fade .18s ease-out;}',
            '.jsr-dialog{animation:jsr-pop .26s cubic-bezier(.16,1,.3,1);}',
            '.jsr-skel{animation:jsr-pulse 1.2s ease-in-out infinite alternate;}',
            '}',
            '@media (prefers-reduced-motion:reduce){.jsr-toast,.jsr-btn,.jsr-row,#' + BUTTON_ID + '{transition:none;}}',
            '@keyframes jsr-fade{from{opacity:0;}}',
            '@keyframes jsr-pop{from{opacity:0;transform:translateY(12px) scale(.98);}}',
            '@keyframes jsr-pulse{to{opacity:.45;}}',

            '@media (max-width:600px){.jsr-overlay{padding:0;align-items:flex-end;}.jsr-dialog{max-width:none;border-radius:16px 16px 0 0;}.jsr-poster{width:5em;}.jsr-head,.jsr-scroll,.jsr-footer{padding-left:1em;padding-right:1em;}.jsr-has-hero .jsr-head{padding-top:4em;}}'
        ].join('\n');
        var style = document.createElement('style');
        style.id = 'jsr-styles';
        style.textContent = css;
        document.head.appendChild(style);
    }

    // ---------------------------------------------------------------- detail page

    function parseVirtualId(id) {
        var m = VIRTUAL_ID.exec(String(id || '').replace(/-/g, ''));
        if (!m) {
            return null;
        }
        var kind = parseInt(m[1], 16);
        return {
            mediaType: kind === 1 ? 'movie' : 'tv',
            season: kind === 3 ? parseInt(m[2], 16) : null,
            tmdbId: parseInt(m[3], 16)
        };
    }

    function currentDetailsId() {
        var hash = window.location.hash || '';
        if (!/^#!?\/details/i.test(hash)) {
            return null;
        }
        var qIndex = hash.indexOf('?');
        return new URLSearchParams(qIndex >= 0 ? hash.slice(qIndex + 1) : '').get('id');
    }

    function visibleDetailPage() {
        var pages = document.querySelectorAll('#itemDetailPage:not(.hide), .itemDetailPage:not(.hide)');
        for (var i = pages.length - 1; i >= 0; i--) {
            if (pages[i].getClientRects().length) {
                return pages[i];
            }
        }
        return null;
    }

    function removeButton() {
        var btn = document.getElementById(BUTTON_ID);
        if (btn) {
            btn.remove();
        }
    }

    // Resolves to true when something about this title can still be requested (cached per page view).
    function isRequestable(virtualId) {
        var key = JSON.stringify(virtualId);
        if (!state.requestable[key]) {
            state.requestable[key] = (virtualId.mediaType === 'movie'
                ? api('GET', 'Title/movie/' + virtualId.tmdbId).then(function (item) {
                    return canRequestStatus(item.status) || !!(state.config && state.config.enable4K && canRequestStatus(item.status4K));
                })
                : api('GET', 'Tv/' + virtualId.tmdbId).then(function (tv) {
                    return (tv.seasons || []).some(function (s) {
                        return (virtualId.season == null || s.seasonNumber === virtualId.season) && canRequestStatus(s.status);
                    });
                })).catch(function () { return false; });
        }
        return state.requestable[key];
    }

    function ensureButton(virtualId) {
        isRequestable(virtualId).then(function (requestable) {
            var id = currentDetailsId();
            var current = id && parseVirtualId(id);
            if (requestable && current && JSON.stringify(current) === JSON.stringify(virtualId)) {
                placeButton(virtualId);
            } else if (!requestable) {
                removeButton();
            }
        });
    }

    function placeButton(virtualId) {
        var page = visibleDetailPage();
        var container = page && page.querySelector('.mainDetailButtons, .detailButtons');
        var btn = document.getElementById(BUTTON_ID);
        if (btn && btn.getAttribute('data-key') === JSON.stringify(virtualId)
            && ((container && container.contains(btn)) || (!container && btn.classList.contains('jsr-floating')))) {
            return;
        }
        if (btn) {
            btn.remove();
        }

        btn = document.createElement('button');
        btn.id = BUTTON_ID;
        btn.type = 'button';
        btn.setAttribute('data-key', JSON.stringify(virtualId));
        btn.innerHTML = icon('add') + '<span>Request</span>';
        btn.title = 'Request with ' + ((state.config && state.config.serverName) || 'Jellyseerr');
        if (container) {
            btn.className = 'button-flat detailButton';
            container.insertBefore(btn, container.firstChild);
        } else {
            btn.className = 'jsr-floating';
            document.body.appendChild(btn);
        }
        btn.addEventListener('click', function () {
            if (btn.getAttribute('aria-busy') === 'true') {
                return;
            }
            btn.setAttribute('aria-busy', 'true');
            Promise.all([api('GET', 'Title/' + virtualId.mediaType + '/' + virtualId.tmdbId), getConfig()]).then(function (res) {
                openDialog(res[0], virtualId.season);
            }).catch(function (err) {
                toast(err.message, true);
            }).then(function () {
                btn.removeAttribute('aria-busy');
            });
        });
    }

    function tick() {
        if (apiClient() && isHomeRoute()) {
            getConfig().then(function (config) {
                if (config.configured && config.discoverRows) {
                    ensureDiscoverRows(config);
                }
            }, function () {});
        }

        var id = currentDetailsId();
        var virtualId = id && parseVirtualId(id);
        document.body.classList.toggle('jsr-virtual-details', !!virtualId);
        if (!virtualId || !apiClient()) {
            removeButton();
            state.requestable = {};
            return;
        }
        getConfig().then(function (config) {
            if (config.requestButton) {
                ensureButton(virtualId);
            }
        }, function () {});
    }

    // ---------------------------------------------------------------- home page rows

    function isHomeRoute() {
        var hash = window.location.hash || '';
        return /^#!?\/home(\.html)?(\?|$)/i.test(hash) || hash === '' || hash === '#' || hash === '#/';
    }

    // The home tab's sections container (Jellyfin rebuilds it now and then, so this is checked on every tick).
    function homeSectionsContainer() {
        var nodes = document.querySelectorAll('#indexPage:not(.hide) .homeSectionsContainer, .homePage:not(.hide) .homeSectionsContainer, #homeTab .sections');
        for (var i = nodes.length - 1; i >= 0; i--) {
            if (nodes[i].getClientRects().length) {
                return nodes[i];
            }
        }
        return null;
    }

    function loadDiscover() {
        if (state.discover && Date.now() - state.discoverAt < DISCOVER_TTL) {
            return Promise.resolve(state.discover);
        }
        if (!state.discoverPromise) {
            state.discoverPromise = Promise.all(DISCOVER_ROWS.map(function (row) {
                return api('GET', 'Discover/' + row.list).then(function (res) {
                    return (res && res.results) || [];
                }, function (err) {
                    console.warn('Jellyseerr Requester: could not load ' + row.list, err);
                    return [];
                });
            })).then(function (lists) {
                state.discover = lists;
                state.discoverAt = Date.now();
                state.discoverPromise = null;
                return lists;
            });
        }
        return state.discoverPromise;
    }

    function ensureDiscoverRows(config) {
        var container = homeSectionsContainer();
        if (!container || container.querySelector('.jsr-discover')) {
            return;
        }
        loadDiscover().then(function (lists) {
            // Re-check: the page may have changed or another tick may have added the rows meanwhile.
            var target = homeSectionsContainer();
            if (!target || target.querySelector('.jsr-discover') || !isHomeRoute()) {
                return;
            }
            var wrap = document.createElement('div');
            wrap.className = 'jsr-discover';
            DISCOVER_ROWS.forEach(function (row, i) {
                if (lists[i] && lists[i].length) {
                    wrap.appendChild(discoverSection(row, lists[i], config));
                }
            });
            if (wrap.childNodes.length) {
                target.appendChild(wrap);
            }
        });
    }

    function discoverSection(row, items, config) {
        var client = apiClient();
        var serverId = client.serverId ? client.serverId() : '';
        var serverName = config.serverName || 'Jellyseerr';
        var section = document.createElement('div');
        section.className = 'verticalSection jsr-discover-section';
        section.innerHTML =
            '<div class="sectionTitleContainer sectionTitleContainer-cards padded-left padded-right jsr-discover-head">' +
            '<h2 class="sectionTitle sectionTitle-cards">' + escapeHtml(row.title) + '</h2>' +
            (config.publicUrl
                ? '<a class="jsr-discover-link" target="_blank" rel="noopener" href="' + escapeHtml(config.publicUrl + '/discover/' + row.list) + '">' +
                  escapeHtml('Open in ' + serverName) + icon('open_in_new') + '</a>'
                : '') +
            '<button type="button" class="jsr-discover-nav" data-step="-1" aria-label="Scroll left">' + icon('chevron_left') + '</button>' +
            '<button type="button" class="jsr-discover-nav" data-step="1" aria-label="Scroll right">' + icon('chevron_right') + '</button>' +
            '</div>' +
            '<div class="jsr-discover-track padded-left padded-right">' +
            items.map(function (item) { return discoverCard(item, client, serverId, serverName); }).join('') +
            '</div>';

        var track = section.querySelector('.jsr-discover-track');
        section.querySelectorAll('.jsr-discover-nav').forEach(function (btn) {
            btn.addEventListener('click', function () {
                track.scrollBy({ left: Number(btn.getAttribute('data-step')) * track.clientWidth * 0.8, behavior: 'smooth' });
            });
        });
        return section;
    }

    function discoverCard(item, client, serverId, serverName) {
        var params = { maxHeight: 450, quality: 90 };
        if (item.imageTag) {
            params.tag = item.imageTag;
        }
        var img = client.getUrl('Items/' + item.itemId + '/Images/Primary', params);
        var href = '#/details?id=' + encodeURIComponent(item.itemId) + (serverId ? '&serverId=' + encodeURIComponent(serverId) : '');
        var label = item.title + (item.year ? ' (' + item.year + ')' : '') + (item.inLibrary ? ', in your library' : ', from ' + serverName);
        return '<a class="card overflowPortraitCard card-hoverable" href="' + escapeHtml(href) + '" title="' + escapeHtml(label) + '" aria-label="' + escapeHtml(label) + '">' +
            '<div class="cardBox cardBox-bottompadded">' +
            '<div class="cardScalable">' +
            '<div class="cardPadder cardPadder-overflowPortrait"></div>' +
            '<div class="cardImageContainer coveredImage cardContent">' +
            '<img class="jsr-card-img" loading="lazy" decoding="async" alt="" src="' + escapeHtml(img) + '">' +
            (item.inLibrary ? '<span class="jsr-card-flag" title="In your library">' + icon('check') + '</span>' : '') +
            '</div></div>' +
            '<div class="cardText cardTextCentered cardText-first"><bdi>' + escapeHtml(item.title) + '</bdi></div>' +
            '<div class="cardText cardTextCentered cardText-secondary"><bdi>' + escapeHtml(item.year || '') + '</bdi></div>' +
            '</div></a>';
    }

    // ---------------------------------------------------------------- request dialog

    function canRequestStatus(status) {
        return !status || status === STATUS.UNKNOWN || status === STATUS.DELETED;
    }

    function pill(status) {
        return STATUS_LABEL[status] ? '<span class="jsr-pill jsr-pill-' + status + '">' + STATUS_LABEL[status] + '</span>' : '';
    }

    function notice(iconName, message) {
        return '<div class="jsr-notice">' + icon(iconName) + '<span>' + escapeHtml(message) + '</span></div>';
    }

    function formatRuntime(minutes) {
        if (!minutes) {
            return null;
        }
        var h = Math.floor(minutes / 60);
        var m = minutes % 60;
        return h ? h + 'h' + (m ? ' ' + m + 'm' : '') : m + 'm';
    }

    // Standard / 4K switch. Calls onChange('hd' | '4k').
    function qualitySwitch(initial, onChange) {
        var wrap = document.createElement('div');
        wrap.innerHTML = '<div class="jsr-field-label" id="jsr-quality-label">Quality</div>' +
            '<div class="jsr-segment" role="radiogroup" aria-labelledby="jsr-quality-label">' +
            '<button type="button" role="radio" data-q="hd">Standard</button>' +
            '<button type="button" role="radio" data-q="4k">4K</button>' +
            '</div>';
        var buttons = wrap.querySelectorAll('[data-q]');
        function select(q) {
            buttons.forEach(function (b) { b.setAttribute('aria-checked', String(b.getAttribute('data-q') === q)); });
        }
        buttons.forEach(function (b) {
            b.addEventListener('click', function () {
                var q = b.getAttribute('data-q');
                select(q);
                onChange(q);
            });
        });
        select(initial);
        return wrap;
    }

    function openDialog(item, preselectSeason) {
        if (!item) {
            return;
        }
        var config = state.config || {};
        var previousFocus = document.activeElement;
        var previousOverflow = document.documentElement.style.overflow;
        var publicUrl = config.publicUrl ? config.publicUrl + '/' + item.mediaType + '/' + item.id : null;

        var meta = [
            item.mediaType === 'tv' ? 'TV series' : 'Movie',
            item.year,
            item.mediaType === 'movie' ? formatRuntime(item.runtimeMinutes) : null
        ].filter(Boolean).map(function (m) { return '<span>' + escapeHtml(m) + '</span>'; });
        if (item.rating) {
            meta.push('<span>' + icon('star') + Number(item.rating).toFixed(1) + '</span>');
        }

        var overlay = document.createElement('div');
        overlay.className = uiClass() + ' jsr-overlay';
        overlay.innerHTML =
            '<div class="jsr-dialog' + (item.backdropPath ? ' jsr-has-hero' : '') + '" role="dialog" aria-modal="true" aria-labelledby="jsr-dialog-title">' +
            (item.backdropPath ? '<div class="jsr-hero" aria-hidden="true"></div>' : '') +
            '<button type="button" class="jsr-close" aria-label="Close">' + icon('close') + '</button>' +
            '<div class="jsr-head">' +
            (item.posterPath ? '<img class="jsr-poster" alt="" src="' + POSTER_BASE + escapeHtml(item.posterPath) + '">' : '') +
            '<div class="jsr-head-text">' +
            '<h2 id="jsr-dialog-title">' + escapeHtml(item.title) + '</h2>' +
            '<div class="jsr-meta">' + meta.join('') + '</div>' +
            '<div class="jsr-status-slot"></div>' +
            '</div></div>' +
            '<div class="jsr-scroll">' +
            (item.overview
                ? '<p class="jsr-overview">' + escapeHtml(item.overview) + '</p><button type="button" class="jsr-text-btn jsr-more" hidden>Read more</button>'
                : '') +
            '<div class="jsr-body"></div>' +
            '<div class="jsr-error" role="alert" hidden>' + icon('error_outline') + '<span class="jsr-error-text"></span></div>' +
            '</div>' +
            '<div class="jsr-footer">' +
            (publicUrl ? '<a class="jsr-link" target="_blank" rel="noopener" href="' + escapeHtml(publicUrl) + '">' + escapeHtml('Open in ' + (config.serverName || 'Jellyseerr')) + icon('open_in_new') + '</a>' : '') +
            '<button type="button" class="jsr-btn jsr-btn-primary jsr-primary" hidden></button>' +
            '</div></div>';

        var dialog = overlay.querySelector('.jsr-dialog');
        var body = overlay.querySelector('.jsr-body');
        var statusSlot = overlay.querySelector('.jsr-status-slot');
        var errorBox = overlay.querySelector('.jsr-error');
        var primary = overlay.querySelector('.jsr-primary');
        var closeBtn = overlay.querySelector('.jsr-close');
        var onPrimary = null;

        if (item.backdropPath) {
            overlay.querySelector('.jsr-hero').style.backgroundImage = 'url("' + BACKDROP_BASE + item.backdropPath.replace(/["\\]/g, '') + '")';
        }

        function focusables() {
            return Array.prototype.filter.call(
                dialog.querySelectorAll('button:not([disabled]), a[href], input:not([disabled])'),
                function (n) { return !n.hidden && n.getClientRects().length; });
        }

        function close() {
            document.removeEventListener('keydown', onKey, true);
            window.removeEventListener('hashchange', close);
            document.documentElement.style.overflow = previousOverflow;
            overlay.remove();
            if (previousFocus && previousFocus.focus && document.contains(previousFocus)) {
                previousFocus.focus();
            }
        }

        // Keep keyboard and TV-remote focus inside the dialog (Jellyfin's own navigation would move behind it).
        function onKey(e) {
            if (e.key === 'Escape' || e.key === 'Back' || e.key === 'GoBack' || e.key === 'BrowserBack') {
                e.preventDefault();
                e.stopPropagation();
                close();
                return;
            }
            var step = { Tab: e.shiftKey ? -1 : 1, ArrowDown: 1, ArrowRight: 1, ArrowUp: -1, ArrowLeft: -1 }[e.key];
            if (!step) {
                return;
            }
            var list = focusables();
            if (!list.length) {
                return;
            }
            e.preventDefault();
            e.stopPropagation();
            var index = list.indexOf(document.activeElement);
            var next = index < 0 ? (step > 0 ? 0 : list.length - 1) : (index + step + list.length) % list.length;
            list[next].focus();
        }

        function showError(message) {
            errorBox.querySelector('.jsr-error-text').textContent = message || '';
            errorBox.hidden = !message;
        }

        function setPrimary(label, enabled) {
            primary.hidden = !label;
            primary.textContent = label || '';
            primary.disabled = !enabled;
        }

        function focusInitial() {
            (primary.hidden || primary.disabled ? closeBtn : primary).focus();
        }

        overlay.addEventListener('click', function (e) {
            if (e.target === overlay) {
                close();
            }
        });
        closeBtn.addEventListener('click', close);
        primary.addEventListener('click', function () {
            if (onPrimary && !primary.disabled) {
                onPrimary();
            }
        });
        document.addEventListener('keydown', onKey, true);
        window.addEventListener('hashchange', close);
        document.documentElement.style.overflow = 'hidden';
        document.body.appendChild(overlay);

        var overview = overlay.querySelector('.jsr-overview');
        var more = overlay.querySelector('.jsr-more');
        if (overview && overview.scrollHeight > overview.clientHeight + 1) {
            more.hidden = false;
            more.addEventListener('click', function () {
                overview.classList.add('jsr-open');
                more.remove();
                overview.setAttribute('tabindex', '-1');
                overview.focus();
            });
        }

        if (!config.canRequest) {
            statusSlot.innerHTML = pill(item.status);
            body.innerHTML = notice('link_off', config.reason || 'Your account can\'t request from Jellyseerr. Ask your server admin.');
            focusInitial();
            return;
        }

        // After a request, reload so Jellyfin's page shows the new status and poster badge.
        function refreshPage() {
            setTimeout(function () { window.location.reload(); }, 900);
        }

        function submit(payload, restore) {
            var controls = dialog.querySelectorAll('button, input');
            controls.forEach(function (c) { c.disabled = true; });
            closeBtn.disabled = false;
            primary.textContent = 'Requesting…';
            showError('');
            api('POST', 'Request', payload).then(function (res) {
                toast(item.title + ': ' + (res && res.message ? res.message : 'Request submitted.'));
                close();
                refreshPage();
            }, function (err) {
                controls.forEach(function (c) { c.disabled = false; });
                restore();
                showError(err.message);
            });
        }

        if (item.mediaType === 'movie') {
            var movieQuality = !canRequestStatus(item.status) && config.enable4K && canRequestStatus(item.status4K) ? '4k' : 'hd';

            var updateMovie = function () {
                var st = movieQuality === '4k' ? item.status4K : item.status;
                var ok = canRequestStatus(st);
                statusSlot.innerHTML = pill(st);
                setPrimary(ok ? (movieQuality === '4k' ? 'Request in 4K' : 'Request') : (DONE_LABEL[st] || 'Request'), ok);
            };

            onPrimary = function () {
                submit({ mediaType: 'movie', tmdbId: item.id, is4K: movieQuality === '4k' }, updateMovie);
            };

            if (config.enable4K) {
                body.appendChild(qualitySwitch(movieQuality, function (q) {
                    movieQuality = q;
                    showError('');
                    updateMovie();
                }));
            }
            updateMovie();
            focusInitial();
            return;
        }

        // TV: load seasons and let the user pick.
        statusSlot.innerHTML = pill(item.status);
        var switchHost = document.createElement('div');
        var listHost = document.createElement('div');
        body.appendChild(switchHost);
        body.appendChild(listHost);
        listHost.innerHTML = '<div class="jsr-list" aria-busy="true" aria-label="Loading seasons">' +
            '<div class="jsr-skel"></div><div class="jsr-skel"></div><div class="jsr-skel"></div></div>';
        closeBtn.focus();

        api('GET', 'Tv/' + item.id).then(function (tv) {
            if (!document.contains(overlay)) {
                return;
            }
            var seasons = (tv && tv.seasons) || [];
            var quality = 'hd';
            var selected = {};

            function seasonStatus(s) {
                return quality === '4k' ? s.status4K : s.status;
            }

            function requestable() {
                return seasons.filter(function (s) { return canRequestStatus(seasonStatus(s)); });
            }

            function selectedNumbers() {
                return requestable().map(function (s) { return s.seasonNumber; }).filter(function (n) { return selected[n]; });
            }

            // Default: the season this page is about, otherwise everything still missing (same as the heart button).
            function resetSelection() {
                var open = requestable();
                selected = {};
                var only = open.filter(function (s) { return s.seasonNumber === preselectSeason; });
                (only.length ? only : open).forEach(function (s) { selected[s.seasonNumber] = true; });
            }

            function updatePrimary() {
                var open = requestable();
                var n = selectedNumbers().length;
                if (!open.length) {
                    setPrimary(null, false);
                    return;
                }
                var label = n === 0
                    ? 'Select a season'
                    : 'Request ' + (n === open.length && n > 1 ? 'all ' + n + ' seasons' : n + ' season' + (n > 1 ? 's' : ''));
                setPrimary(label + (n && quality === '4k' ? ' in 4K' : ''), n > 0);
            }

            function render() {
                var open = requestable();
                if (!seasons.length) {
                    listHost.innerHTML = notice('info', 'Jellyseerr has no seasons for this show yet.');
                    updatePrimary();
                    return;
                }

                var allOn = open.length > 0 && selectedNumbers().length === open.length;
                listHost.innerHTML =
                    (open.length ? '' : notice('check_circle', 'Every season is already requested or available' + (quality === '4k' ? ' in 4K.' : '.'))) +
                    '<div class="jsr-list-head" style="margin-top:' + (open.length ? '0' : '1em') + '">' +
                    '<span class="jsr-field-label" style="margin:0">Seasons</span>' +
                    (open.length > 1 ? '<button type="button" class="jsr-text-btn jsr-toggle-all">' + (allOn ? 'Clear' : 'Select all') + '</button>' : '') +
                    '</div>' +
                    '<div class="jsr-list">' +
                    seasons.map(function (s) {
                        var st = seasonStatus(s);
                        var name = '<span class="jsr-row-name">' + escapeHtml(s.name || ('Season ' + s.seasonNumber)) + '</span>';
                        var eps = '<span class="jsr-row-sub">' + (s.episodeCount ? s.episodeCount + ' episode' + (s.episodeCount > 1 ? 's' : '') : '') + '</span>';
                        if (!canRequestStatus(st)) {
                            return '<div class="jsr-row jsr-row-locked">' + icon(st === STATUS.AVAILABLE ? 'check_circle' : 'schedule') + name + eps + pill(st) + '</div>';
                        }
                        var on = !!selected[s.seasonNumber];
                        return '<label class="jsr-row' + (on ? ' jsr-row-on' : '') + '">' +
                            '<input type="checkbox" value="' + s.seasonNumber + '"' + (on ? ' checked' : '') + '>' + name + eps + '<span></span></label>';
                    }).join('') +
                    '</div>';

                var toggleAll = listHost.querySelector('.jsr-toggle-all');
                if (toggleAll) {
                    toggleAll.addEventListener('click', function () {
                        var turnOn = selectedNumbers().length !== requestable().length;
                        requestable().forEach(function (s) { selected[s.seasonNumber] = turnOn; });
                        render();
                        listHost.querySelector('.jsr-toggle-all').focus();
                    });
                }
                listHost.querySelectorAll('input[value]').forEach(function (box) {
                    box.addEventListener('change', function () {
                        selected[box.value] = box.checked;
                        box.parentNode.classList.toggle('jsr-row-on', box.checked);
                        if (toggleAll) {
                            toggleAll.textContent = selectedNumbers().length === requestable().length ? 'Clear' : 'Select all';
                        }
                        updatePrimary();
                    });
                });
                updatePrimary();
            }

            onPrimary = function () {
                var numbers = selectedNumbers();
                if (numbers.length) {
                    submit({ mediaType: 'tv', tmdbId: item.id, seasons: numbers, is4K: quality === '4k' }, updatePrimary);
                }
            };

            if (config.enable4K) {
                switchHost.appendChild(qualitySwitch(quality, function (q) {
                    quality = q;
                    statusSlot.innerHTML = pill(q === '4k' ? item.status4K : item.status);
                    showError('');
                    resetSelection();
                    render();
                }));
            }

            resetSelection();
            render();
            focusInitial();
        }, function (err) {
            listHost.innerHTML = '';
            showError('Could not load seasons: ' + err.message);
        });
    }

    // ---------------------------------------------------------------- bootstrap

    injectStyles();
    setInterval(tick, 700);
    window.addEventListener('hashchange', tick);
    window.addEventListener('popstate', tick);
})();
