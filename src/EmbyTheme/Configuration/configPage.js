define(['baseView', 'loading', 'emby-input', 'emby-button', 'emby-checkbox', 'emby-select', 'emby-scroller'], function (BaseView, loading) {
    'use strict';

    var PAGE_SIZE = 50;

    var STATUS_LABELS = {
        Saved: 'Saved',
        Existing: 'Already had one',
        NotFound: 'Not found',
        NoId: 'No usable id',
        Failed: 'Failed',
        Pending: 'Not checked yet',
        Skipped: 'Skipped'
    };

    function url(path) {
        return ApiClient.getUrl('EmbyTheme/' + path);
    }

    function headers(extra) {
        var h = { 'X-Emby-Token': ApiClient.accessToken() };
        for (var k in extra || {}) {
            h[k] = extra[k];
        }
        return h;
    }

    function check(r) {
        if (!r.ok) {
            return r.text().then(function (t) {
                throw new Error('HTTP ' + r.status + (t ? ': ' + t.substring(0, 200) : ''));
            });
        }
        return r.json();
    }

    function getJson(path) {
        return fetch(url(path), { headers: headers(), cache: 'no-store' }).then(check);
    }

    function post(path, body) {
        return fetch(url(path), {
            method: 'POST',
            headers: headers(body ? { 'Content-Type': 'application/json' } : {}),
            body: body ? JSON.stringify(body) : undefined
        }).then(check);
    }

    function esc(s) {
        return String(s === null || s === undefined ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    function when(iso) {
        if (!iso) {
            return '';
        }
        var d = new Date(iso);
        return isNaN(d) ? '' : d.toLocaleString();
    }

    function View(view, params) {
        BaseView.apply(this, arguments);
        var self = this;
        this.config = null;
        this.start = 0;
        this.total = 0;
        this.searchTimer = null;
        this.statusTimer = null;

        view.querySelector('.et-form').addEventListener('submit', function (e) {
            e.preventDefault();
            self.save();
            return false;
        });

        view.querySelectorAll('.btnReset').forEach(function (btn) {
            btn.addEventListener('click', function () {
                self.reset(btn.getAttribute('data-statuses'));
            });
        });

        view.querySelector('.et-tiles').addEventListener('click', function (e) {
            var tile = e.target.closest('[data-status]');
            if (tile) {
                view.querySelector('.selStatus').value = tile.getAttribute('data-status');
                self.start = 0;
                self.loadList();
            }
        });

        view.querySelector('.selStatus').addEventListener('change', function () {
            self.start = 0;
            self.loadList();
        });

        view.querySelector('.txtSearch').addEventListener('input', function () {
            clearTimeout(self.searchTimer);
            self.searchTimer = setTimeout(function () {
                self.start = 0;
                self.loadList();
            }, 300);
        });

        view.querySelector('.btnPrev').addEventListener('click', function () {
            self.start = Math.max(0, self.start - PAGE_SIZE);
            self.loadList();
        });

        view.querySelector('.btnNext').addEventListener('click', function () {
            if (self.start + PAGE_SIZE < self.total) {
                self.start += PAGE_SIZE;
                self.loadList();
            }
        });

        view.querySelector('.et-table tbody').addEventListener('click', function (e) {
            var btn = e.target.closest('button[data-action]');
            if (btn) {
                self.rowAction(btn.getAttribute('data-action'), btn.getAttribute('data-id'), btn);
            }
        });

        view.querySelector('.et-table tbody').addEventListener('submit', function (e) {
            e.preventDefault();
            var form = e.target;
            self.setUrl(form.getAttribute('data-id'), form.querySelector('input').value, form);
        });
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.onResume = function () {
        BaseView.prototype.onResume.apply(this, arguments);
        var self = this;
        loading.show();
        clearTimeout(this.statusTimer);
        Promise.all([getJson('Settings'), getJson('Status')]).then(function (res) {
            self.config = res[0];
            self.fill(res[0]);
            self.renderStatus(res[1]);
            self.renderLibraries(res[1].Libraries || [], res[0].LibraryIds || []);
            loading.hide();
            self.loadList();
            self.pollStatus();
        }).catch(function (err) {
            loading.hide();
            self.msg('.et-savemsg', 'Could not load settings: ' + err.message, true);
        });
    };

    View.prototype.onPause = function () {
        clearTimeout(this.statusTimer);
        if (BaseView.prototype.onPause) {
            BaseView.prototype.onPause.apply(this, arguments);
        }
    };

    View.prototype.destroy = function () {
        clearTimeout(this.statusTimer);
        clearTimeout(this.searchTimer);
        BaseView.prototype.destroy.apply(this, arguments);
    };

    // ------------------------------------------------------------------ status

    View.prototype.pollStatus = function () {
        var self = this;
        clearTimeout(this.statusTimer);
        this.statusTimer = setTimeout(function () {
            getJson('Status').then(function (s) {
                self.renderStatus(s);
            }).catch(function () { }).then(function () {
                self.pollStatus();
            });
        }, 15000);
    };

    View.prototype.refreshStatus = function () {
        var self = this;
        return getJson('Status').then(function (s) {
            self.renderStatus(s);
        });
    };

    View.prototype.renderStatus = function (s) {
        var counts = s.Counts || {};
        var order = ['Saved', 'Existing', 'NotFound', 'NoId', 'Failed', 'Pending', 'Skipped'];
        this.view.querySelector('.et-tiles').innerHTML = order.map(function (k) {
            return '<div class="et-tile ' + k.toLowerCase() + '" data-status="' + k + '"><b>' + (counts[k] || 0).toLocaleString() + '</b><span>' + STATUS_LABELS[k] + '</span></div>';
        }).join('');

        var r = s.LastRun;
        var lines = [];
        if (s.IsRunning) {
            lines.push('<b>A download run is in progress.</b>');
        }
        if (r) {
            lines.push('Last run ' + esc(when(r.FinishedUtc)) + ': ' + r.Checked + ' checked, ' + r.Saved + ' saved, ' + r.NotFound + ' not on Plex, ' +
                r.NoId + ' no usable id, ' + r.AlreadyHadTheme + ' already had one, ' + r.Failed + ' failed. ' +
                r.Waiting.toLocaleString() + ' waiting for later runs (' + r.SeriesInScope.toLocaleString() + ' series in scope).');
            if (r.Note) {
                lines.push('<span class="et-error">' + esc(r.Note) + '</span>');
            }
        } else if (!s.IsRunning) {
            lines.push('No runs yet. Run the task from Scheduled Tasks, or wait for its schedule.');
        }
        if (!s.TmdbConfigured) {
            lines.push('No TMDB key: series that only have a TMDB id are listed as "No usable id".');
        }
        lines.push('<span class="et-hint">Plugin ' + esc(s.Version || '') + ' &middot; state file <span class="et-mono">' + esc(s.StateFile || '') + '</span></span>');
        this.view.querySelector('.et-lastrun').innerHTML = lines.join('<br>');
    };

    View.prototype.reset = function (statuses) {
        var self = this;
        post('Reset?Statuses=' + encodeURIComponent(statuses)).then(function (res) {
            self.msg('.et-listmsg', res.Reset.toLocaleString() + ' series will be looked up again on the next run.');
            self.refreshStatus();
            self.loadList();
        }).catch(function (err) {
            self.msg('.et-listmsg', 'Reset failed: ' + err.message, true);
        });
    };

    // ------------------------------------------------------------------ settings

    View.prototype.renderLibraries = function (libs, chosen) {
        var box = this.view.querySelector('.et-libs');
        if (!libs.length) {
            box.innerHTML = '<p class="et-hint">No TV libraries found.</p>';
            return;
        }
        box.innerHTML = libs.map(function (l) {
            var checked = chosen.indexOf(l.Id) >= 0 ? ' checked' : '';
            var type = l.CollectionType ? '' : ' <span class="et-hint">(mixed content)</span>';
            return '<label><input type="checkbox" is="emby-checkbox" data-lib="' + esc(l.Id) + '"' + checked + ' /><span>' + esc(l.Name) + type + '</span></label>';
        }).join('');
    };

    View.prototype.fill = function (config) {
        this.view.querySelectorAll('.et-form [data-key]').forEach(function (el) {
            var value = config[el.getAttribute('data-key')];
            if (el.type === 'checkbox') {
                el.checked = !!value;
            } else {
                el.value = value === null || value === undefined ? '' : value;
            }
        });
    };

    View.prototype.collect = function () {
        var options = Object.assign({}, this.config || {});
        this.view.querySelectorAll('.et-form [data-key]').forEach(function (el) {
            var key = el.getAttribute('data-key');
            if (el.type === 'checkbox') {
                options[key] = el.checked;
            } else if (el.type === 'number') {
                options[key] = parseInt(el.value, 10) || 0;
            } else {
                options[key] = el.value;
            }
        });
        options.LibraryIds = Array.prototype.map.call(this.view.querySelectorAll('[data-lib]:checked'), function (el) {
            return el.getAttribute('data-lib');
        });
        return options;
    };

    View.prototype.save = function () {
        var self = this;
        loading.show();
        post('Settings', this.collect()).then(function (saved) {
            self.config = saved;
            self.fill(saved);
            loading.hide();
            self.msg('.et-savemsg', 'Saved.');
            self.refreshStatus();
        }).catch(function (err) {
            loading.hide();
            self.msg('.et-savemsg', 'Save failed: ' + err.message, true);
        });
    };

    // ------------------------------------------------------------------ series list

    View.prototype.loadList = function () {
        var self = this;
        var status = this.view.querySelector('.selStatus').value;
        var search = this.view.querySelector('.txtSearch').value;
        var q = 'Series?StartIndex=' + this.start + '&Limit=' + PAGE_SIZE +
            (status ? '&Status=' + encodeURIComponent(status) : '') +
            (search ? '&Search=' + encodeURIComponent(search) : '');
        getJson(q).then(function (res) {
            self.total = res.TotalRecordCount;
            self.view.querySelector('.et-table tbody').innerHTML = res.Items.length
                ? res.Items.map(self.rowHtml).join('')
                : '<tr><td colspan="5" class="et-hint">No series here yet. They appear after the first run.</td></tr>';
            var end = Math.min(self.start + PAGE_SIZE, self.total);
            self.view.querySelector('.et-pageinfo').textContent = self.total ? (self.start + 1) + '–' + end + ' of ' + self.total.toLocaleString() : '';
            self.view.querySelector('.btnPrev').disabled = self.start === 0;
            self.view.querySelector('.btnNext').disabled = end >= self.total;
        }).catch(function (err) {
            self.msg('.et-listmsg', 'Could not load series: ' + err.message, true);
        });
    };

    View.prototype.rowHtml = function (e) {
        var ids = [];
        if (e.Tvdb) {
            ids.push('TVDB ' + esc(e.Tvdb));
        } else if (e.ResolvedTvdb) {
            ids.push('TVDB ' + esc(e.ResolvedTvdb) + ' (via TMDB)');
        }
        if (e.Tmdb) {
            ids.push('TMDB ' + esc(e.Tmdb));
        }
        var detail = e.Error ? esc(e.Error) : (e.Source ? 'from ' + esc(e.Source) : '');
        if (e.CheckedUtc) {
            detail += (detail ? '<br>' : '') + '<span class="et-hint">' + esc(when(e.CheckedUtc)) + '</span>';
        }
        var skip = e.Status === 'Skipped'
            ? '<button is="emby-button" type="button" class="raised" data-action="unskip" data-id="' + esc(e.Id) + '">Unskip</button>'
            : '<button is="emby-button" type="button" class="raised" data-action="skip" data-id="' + esc(e.Id) + '">Skip</button>';
        return '<tr data-row="' + esc(e.Id) + '">' +
            '<td>' + esc(e.Name) + '</td>' +
            '<td class="et-mono">' + ids.join('<br>') + '</td>' +
            '<td><span class="et-pill ' + esc(e.Status) + '">' + esc(STATUS_LABELS[e.Status] || e.Status) + '</span></td>' +
            '<td class="et-err">' + detail + '</td>' +
            '<td class="et-rowbtns">' +
            '<button is="emby-button" type="button" class="raised" data-action="fetch" data-id="' + esc(e.Id) + '">Fetch</button>' +
            '<button is="emby-button" type="button" class="raised" data-action="url" data-id="' + esc(e.Id) + '">Set URL</button>' +
            skip + '</td></tr>';
    };

    View.prototype.replaceRow = function (entry) {
        var row = this.view.querySelector('tr[data-row="' + entry.Id + '"]');
        if (row) {
            var next = row.nextElementSibling;
            if (next && next.classList.contains('et-urlrow')) {
                next.remove();
            }
            row.outerHTML = this.rowHtml(entry);
        }
    };

    View.prototype.rowAction = function (action, id, btn) {
        var self = this;
        if (action === 'url') {
            var row = this.view.querySelector('tr[data-row="' + id + '"]');
            var next = row.nextElementSibling;
            if (next && next.classList.contains('et-urlrow')) {
                next.remove();
                return;
            }
            row.insertAdjacentHTML('afterend', '<tr class="et-urlrow"><td colspan="5"><form data-id="' + esc(id) + '">' +
                '<input type="url" class="et-textinput" placeholder="https://…/theme.mp3" required />' +
                '<button is="emby-button" type="submit" class="raised button-submit">Save as theme</button></form></td></tr>');
            row.nextElementSibling.querySelector('input').focus();
            return;
        }

        btn.disabled = true;
        var call = action === 'fetch' ? post('Series/' + encodeURIComponent(id) + '/Fetch')
            : post('Series/' + encodeURIComponent(id) + '/Skip?Skip=' + (action === 'skip'));
        call.then(function (res) {
            self.replaceRow(res.Entry);
            if (action === 'fetch') {
                self.msg('.et-listmsg', res.Entry.Name + ': ' + (STATUS_LABELS[res.Status] || res.Status) + (res.Detail ? ' (' + res.Detail + ')' : ''), res.Status === 'Failed');
            }
            self.refreshStatus();
        }).catch(function (err) {
            btn.disabled = false;
            self.msg('.et-listmsg', 'Failed: ' + err.message, true);
        });
    };

    View.prototype.setUrl = function (id, link, form) {
        var self = this;
        var button = form.querySelector('button');
        button.disabled = true;
        post('Series/' + encodeURIComponent(id) + '/Url?Url=' + encodeURIComponent(link)).then(function (res) {
            self.replaceRow(res.Entry);
            self.msg('.et-listmsg', res.Entry.Name + ': ' + (res.Status === 'Saved' ? 'theme saved' : 'not saved (' + res.Detail + ')'), res.Status !== 'Saved');
            self.refreshStatus();
        }).catch(function (err) {
            button.disabled = false;
            self.msg('.et-listmsg', 'Failed: ' + err.message, true);
        });
    };

    View.prototype.msg = function (selector, text, isError) {
        var el = this.view.querySelector(selector);
        el.textContent = text;
        el.classList.toggle('et-error', !!isError);
    };

    return View;
});
