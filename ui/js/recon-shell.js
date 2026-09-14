/* =====================================================================
   Portal shell: header, sidebar, and the formatting helpers every screen
   shares. Rendered in JavaScript rather than fetched as a partial so the
   pages work when opened straight from the filesystem.

   jQuery + Bootstrap 5 only. No framework, no build step.
   ===================================================================== */

window.Recon = (function ($) {
    "use strict";

    var NAV = [
        { group: "Operations", items: [
            { href: "index.html",      icon: "speedometer2", label: "Run dashboard" },
            { href: "exceptions.html", icon: "exclamation-triangle", label: "Exceptions" }
        ] },
        { group: "Configuration", items: [
            { href: "counterparties.html", icon: "diagram-3", label: "Counterparties" },
            { href: "datasets.html",       icon: "table", label: "Datasets & fields" },
            { href: "rules.html",          icon: "sliders", label: "Rule builder" },
            { href: "settings.html",       icon: "gear", label: "Platform settings" }
        ] }
    ];

    /* ---- formatting -------------------------------------------------
       Amounts are stored and compared as integer MINOR UNITS. They are
       converted for DISPLAY ONLY, here, using the currency's minorUnits.
       Nothing in this file ever does arithmetic on the decimal form.
       ----------------------------------------------------------------- */

    function minorUnitsOf(code) {
        var c = window.ReconMock && window.ReconMock.currencies[code];
        return c ? c.minorUnits : 3;
    }

    function formatMinor(amountMinor, currencyCode) {
        if (amountMinor === null || amountMinor === undefined) { return "—"; }
        var units = minorUnitsOf(currencyCode);
        var neg = amountMinor < 0;
        var abs = Math.abs(amountMinor).toString();
        while (abs.length <= units) { abs = "0" + abs; }
        var whole = units > 0 ? abs.slice(0, abs.length - units) : abs;
        var frac = units > 0 ? abs.slice(abs.length - units) : "";
        whole = whole.replace(/\B(?=(\d{3})+(?!\d))/g, ",");
        return (neg ? "-" : "") + whole + (frac ? "." + frac : "");
    }

    function formatCount(n) {
        if (n === null || n === undefined) { return "—"; }
        return n.toString().replace(/\B(?=(\d{3})+(?!\d))/g, ",");
    }

    function formatDuration(sec) {
        if (sec === null || sec === undefined) { return "—"; }
        var m = Math.floor(sec / 60), s = sec % 60;
        return m + "m " + (s < 10 ? "0" : "") + s + "s";
    }

    function escapeHtml(s) {
        if (s === null || s === undefined) { return ""; }
        return String(s)
            .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;").replace(/'/g, "&#39;");
    }

    /* ---- status presentation ---------------------------------------- */

    function statusBadge(status) {
        var map = {
            Completed: "success", Running: "primary", Pending: "secondary",
            Failed: "danger", Cancelled: "secondary", Rejected: "warning",
            Resuming: "info",
            Open: "danger", InProgress: "warning", Resolved: "success",
            AutoClosed: "info", WrittenOff: "secondary"
        };
        var tone = map[status] || "secondary";
        return '<span class="badge text-bg-' + tone + '">' + escapeHtml(status) + "</span>";
    }

    function runTypeBadge(type) {
        var tone = (type === "Sandbox") ? "warning"
                 : (type === "Rematch" || type === "Rerun") ? "info" : "light";
        var cls = (tone === "light") ? "badge text-bg-light border" : "badge text-bg-" + tone;
        return '<span class="' + cls + '">' + escapeHtml(type) + "</span>";
    }

    /* ---- shell ------------------------------------------------------ */

    function currentPage() {
        var path = window.location.pathname.split("/").pop();
        return path || "index.html";
    }

    function navHtml(active) {
        var html = "";
        $.each(NAV, function (_, section) {
            html += '<div class="rc-nav-group">' + escapeHtml(section.group) + "</div>";
            $.each(section.items, function (__, item) {
                var isActive = (item.href === active);
                html += '<a class="nav-link' + (isActive ? " active" : "") + '" href="'
                     + item.href + '"'
                     + (isActive ? ' aria-current="page"' : "") + ">"
                     + '<i class="bi bi-' + item.icon + '" aria-hidden="true"></i>'
                     + '<span>' + escapeHtml(item.label) + "</span></a>";
            });
        });
        return html;
    }

    function renderShell(opts) {
        var active = currentPage();
        var nav = navHtml(active);

        $("#rc-header").html(
            '<div class="d-flex align-items-center h-100 px-3 gap-3">'
          + '  <button class="btn btn-sm btn-outline-secondary d-lg-none" type="button"'
          + '          data-bs-toggle="offcanvas" data-bs-target="#rc-offcanvas"'
          + '          aria-controls="rc-offcanvas" aria-label="Open navigation">'
          + '    <i class="bi bi-list" aria-hidden="true"></i>'
          + "  </button>"
          + '  <a class="rc-brand text-decoration-none" href="index.html">Reconciliation Platform</a>'
          + '  <span class="badge text-bg-light border d-none d-md-inline">v1.0 prototype</span>'
          + '  <div class="ms-auto d-flex align-items-center gap-3">'
          + '    <span class="rc-help d-none d-sm-inline">Asia/Amman · UTC+3</span>'
          + '    <span class="d-inline-flex align-items-center gap-2">'
          + '      <i class="bi bi-person-circle" aria-hidden="true"></i>'
          + '      <span class="d-none d-sm-inline">ops.hala</span>'
          + "    </span>"
          + "  </div>"
          + "</div>"
        );

        $("#rc-sidebar").html('<nav class="nav flex-column" aria-label="Main">' + nav + "</nav>");
        $("#rc-offcanvas-body").html('<nav class="nav flex-column" aria-label="Main">' + nav + "</nav>");

        if (opts && opts.title) {
            document.title = opts.title + " · Reconciliation Platform";
        }
    }

    /* ---- small helpers used by more than one screen ----------------- */

    /* Builds the <option> list for a field dropdown from a dataset's
       FIELD REGISTRY. Rule conditions resolve field names through this
       and only this — never from free user text. That is the design's
       single most important safety property (§6), so the dropdown is
       also where IsMatchable is enforced. */
    function fieldOptions(dataset, selectedId, opts) {
        opts = opts || {};
        var html = '<option value="">— choose a field —</option>';
        if (!dataset) { return html; }
        $.each(dataset.fields, function (_, f) {
            if (opts.matchableOnly !== false && !f.matchable) { return; }
            if (opts.type && f.type !== opts.type) { return; }
            html += '<option value="' + f.id + '"'
                 + (f.id === selectedId ? " selected" : "") + ">"
                 + escapeHtml(f.label) + " (" + escapeHtml(f.code) + ")"
                 + (f.indexed ? " · indexed" : "")
                 + "</option>";
        });
        return html;
    }

    function roleBadge(role) {
        if (!role) { return '<span class="text-muted">—</span>'; }
        var tone = {
            Reference: "primary", OriginalReference: "primary", Amount: "success",
            Currency: "secondary", Date: "info", Direction: "dark",
            Status: "warning", Party: "secondary", TransactionType: "secondary"
        }[role] || "light";
        var cls = tone === "light" ? "badge text-bg-light border rc-badge-role"
                                   : "badge text-bg-" + tone + " rc-badge-role";
        return '<span class="' + cls + '">' + escapeHtml(role) + "</span>";
    }

    return {
        renderShell: renderShell,
        formatMinor: formatMinor,
        formatCount: formatCount,
        formatDuration: formatDuration,
        escapeHtml: escapeHtml,
        statusBadge: statusBadge,
        runTypeBadge: runTypeBadge,
        fieldOptions: fieldOptions,
        roleBadge: roleBadge,
        minorUnitsOf: minorUnitsOf
    };
}(jQuery));
