/* Datasets & field registry — design §6. The registry is what makes the
   rule builder possible, and what makes the slot storage invisible. */
(function ($) {
    "use strict";

    var M = window.ReconMock, R = window.Recon;
    var selectedId = 1;

    function renderList() {
        var html = "";
        $.each(M.datasets, function (_, d) {
            var cp = null;
            $.each(M.counterparties, function (_, c) {
                if (c.id === d.counterpartyId) { cp = c; }
            });
            var active = (d.id === selectedId);
            html += '<button type="button" class="list-group-item list-group-item-action'
                 + (active ? " active" : "") + '" data-ds="' + d.id + '">'
                 + '<div class="d-flex justify-content-between align-items-start gap-2">'
                 + "<div>"
                 + '<div class="fw-semibold">' + R.escapeHtml(d.name) + "</div>"
                 + '<div class="' + (active ? "text-white-50" : "rc-help") + ' rc-mono">'
                 + R.escapeHtml(d.code) + " · " + R.escapeHtml(cp ? cp.code : "—") + "</div>"
                 + "</div>"
                 + "<div class='text-end'>"
                 + '<span class="badge text-bg-' + (active ? "light text-dark" : "light border") + '">'
                 + R.escapeHtml(d.provider) + "</span>"
                 + (d.active ? "" : '<div class="' + (active ? "text-white-50" : "rc-help")
                                  + ' mt-1">not activated</div>')
                 + "</div></div></button>";
        });
        $("#ds-list").html(html);
    }

    function renderRegistry() {
        var d = M.datasetById(selectedId);
        $("#fr-title").text("Field registry — " + (d ? d.name : ""));
        $("#fr-sub").html(d
            ? R.escapeHtml(d.code) + " · " + R.escapeHtml(d.provider) + " · "
              + R.escapeHtml(d.currency) + " (" + R.minorUnitsOf(d.currency) + " minor units)"
            : "");

        if (!d || d.fields.length === 0) {
            $("#fr-rows").html('<tr><td colspan="8" class="rc-empty">'
                + "No fields mapped yet. Upload a sample file and the system proposes a registry from it."
                + "</td></tr>");
            renderValidation(d);
            renderSlotUsage(d);
            return;
        }

        var html = "";
        $.each(d.fields, function (_, f) {
            html += "<tr>"
                 + '<td><code class="rc-field">' + R.escapeHtml(f.code) + "</code></td>"
                 + "<td>" + R.escapeHtml(f.label) + "</td>"
                 + '<td class="rc-help">' + R.escapeHtml(f.type) + "</td>"
                 + "<td>" + R.roleBadge(f.role) + "</td>"
                 + '<td class="rc-mono">' + R.escapeHtml(f.slot) + "</td>"
                 + '<td class="text-center">' + (f.matchable
                     ? '<i class="bi bi-check-lg text-success" title="may appear in a rule"></i>'
                     : '<i class="bi bi-dash text-muted" title="not available to the rule builder"></i>')
                 + "</td>"
                 + '<td class="text-center">' + (f.indexed
                     ? '<i class="bi bi-lightning-charge-fill text-warning" title="nonclustered index maintained"></i>'
                     : '<i class="bi bi-dash text-muted"></i>')
                 + "</td>"
                 + "<td>" + (f.normalize
                     ? '<span class="rc-mono">' + R.escapeHtml(f.normalizedSlot) + "</span>"
                       + '<div class="rc-help">filled at parse time</div>'
                     : '<span class="text-muted">—</span>')
                 + "</td></tr>";
        });
        $("#fr-rows").html(html);
        renderValidation(d);
        renderSlotUsage(d);
    }

    /* B6 · the activation gate. "Cannot activate without
       Reference/Amount/Currency/Date/Direction" was stated in the design
       but had no enforcement point; this is where Operations sees it. */
    function renderValidation(d) {
        if (!d) { $("#fr-validation").html(""); return; }

        var REQUIRED = ["Reference", "Amount", "Currency", "Date", "Direction"];
        var have = {};
        $.each(d.fields, function (_, f) { if (f.role) { have[f.role] = true; } });

        var missing = $.grep(REQUIRED, function (role) { return !have[role]; });
        var indexedCount = $.grep(d.fields, function (f) { return f.indexed; }).length;
        var MAX_IDX = 4;

        var html = "";
        if (missing.length === 0) {
            html += '<div class="alert alert-success py-2 px-3 mb-2 small">'
                 + '<i class="bi bi-check-circle"></i> <strong>Ready to activate.</strong> '
                 + "All universal roles are mapped: " + REQUIRED.join(", ") + "."
                 + "</div>";
        } else {
            html += '<div class="alert alert-warning py-2 px-3 mb-2 small">'
                 + '<i class="bi bi-exclamation-triangle"></i> <strong>Cannot activate.</strong> '
                 + "Missing universal role" + (missing.length > 1 ? "s" : "") + ": <strong>"
                 + missing.join(", ") + "</strong>. Control totals, partitioning and fee logic "
                 + "all rely on these, so the definition is blocked until they are mapped."
                 + "</div>";
        }
        html += '<div class="small rc-help">Matching indexes: <strong>' + indexedCount
             + " of " + MAX_IDX + "</strong> used. Generated at activation from the "
             + "pass-1 and pass-2 conditions — every extra index is a 2M-row "
             + "maintenance cost on every load.</div>";
        $("#fr-validation").html(html);
    }

    /* The slot budget, made visible. Finding 2: companion slots come from
       their own pool (Text21..Text30), so reserving a normalized field no
       longer silently eats a mapped field's storage. */
    function renderSlotUsage(d) {
        var groups = [
            { key: "Text", label: "Text (reservable)", total: 20, normalized: false },
            { key: "Text", label: "Text (companions)", total: 10, normalized: true },
            { key: "Num", label: "Integer", total: 15, normalized: false },
            { key: "Dec", label: "Decimal", total: 5, normalized: false },
            { key: "Date", label: "Date", total: 8, normalized: false },
            { key: "Flag", label: "Boolean", total: 5, normalized: false }
        ];

        var used = {};
        if (d) {
            $.each(d.fields, function (_, f) {
                used[f.slot] = true;
                if (f.normalizedSlot) { used[f.normalizedSlot] = true; }
            });
        }

        var html = "";
        $.each(groups, function (_, g) {
            var n = 0;
            $.each(M.slots, function (__, s) {
                if (s.type === "String" && g.key === "Text") {
                    if (s.normalizedOnly !== g.normalized) { return; }
                } else if (s.name.indexOf(g.key) !== 0) {
                    return;
                } else if (g.key === "Text") {
                    return;
                }
                if (used[s.name]) { n++; }
            });
            var pct = g.total > 0 ? (100 * n / g.total) : 0;
            html += '<div class="mb-2">'
                 + '<div class="d-flex justify-content-between small">'
                 + "<span>" + R.escapeHtml(g.label) + "</span>"
                 + '<span class="rc-num">' + n + " / " + g.total + "</span></div>"
                 + '<div class="rc-pass-bar"><span style="width:' + pct.toFixed(1) + '%"></span></div>'
                 + "</div>";
        });
        html += '<div class="rc-help mt-2">Widening a pool later means <code>ALTER TABLE</code> '
             + "on a table with hundreds of millions of rows, so the pools are generous "
             + "from the start — empty columns cost almost nothing.</div>";
        $("#slot-usage").html(html);
    }

    $(function () {
        R.renderShell({ title: "Datasets & fields" });
        renderList();
        renderRegistry();

        $("#ds-list").on("click", "[data-ds]", function () {
            selectedId = parseInt($(this).data("ds"), 10);
            renderList();
            renderRegistry();
        });
    });
}(jQuery));
