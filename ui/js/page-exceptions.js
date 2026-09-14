/* Exception workspace — design §13 "Operations".
   Aging is computed here, in the query layer: the v1.0 review removed the
   non-deterministic AgeDays computed column from ops.ReconException (A7). */
(function ($) {
    "use strict";

    var M = window.ReconMock, R = window.Recon;
    var TODAY = new Date("2026-09-14T00:00:00Z");

    function ageDays(businessDate) {
        var d = new Date(businessDate + "T00:00:00Z");
        return Math.round((TODAY - d) / 86400000);
    }

    function labelFor(code) {
        var out = code;
        $.each(M.exceptionCodes, function (_, c) { if (c.code === code) { out = c.label; } });
        return out;
    }

    function severityFor(code) {
        var out = "Normal";
        $.each(M.exceptionCodes, function (_, c) { if (c.code === code) { out = c.severity; } });
        return out;
    }

    function filtered() {
        var code = $("#f-code").val();
        var status = $("#f-exstatus").val();
        var age = $("#f-age").val();
        var q = ($("#f-search").val() || "").toLowerCase();

        return $.grep(M.exceptions, function (e) {
            if (code && e.code !== code) { return false; }
            if (status && e.status !== status) { return false; }
            if (q && e.ref.toLowerCase().indexOf(q) === -1) { return false; }
            if (age !== "") {
                var a = ageDays(e.businessDate);
                var lo = parseInt(age, 10);
                if (lo === 0 && a !== 0) { return false; }
                if (lo === 1 && (a < 1 || a > 2)) { return false; }
                if (lo === 3 && (a < 3 || a > 7)) { return false; }
                if (lo === 8 && a <= 7) { return false; }
            }
            return true;
        });
    }

    function renderTiles(rows) {
        var open = 0, openAmount = 0, aged = 0, ambiguous = 0;
        $.each(rows, function (_, e) {
            if (e.status === "Open" || e.status === "InProgress") {
                open++;
                openAmount += e.amountMinor || 0;
                if (ageDays(e.businessDate) > 7) { aged++; }
            }
            if (e.code === "AMBIGUOUS") { ambiguous++; }
        });

        var tiles = [
            { label: "Open items", value: R.formatCount(open), sub: "including in-progress" },
            { label: "Open value", value: R.formatMinor(openAmount, "JOD") + " JOD",
              sub: "summed from integer minor units" },
            { label: "Aged over 7 days", value: R.formatCount(aged),
              sub: aged > 0 ? "these are the ones that lose trust" : "nothing stale" },
            { label: "Ambiguous", value: R.formatCount(ambiguous),
              sub: "a rule never silently picks one" }
        ];

        var html = "";
        $.each(tiles, function (_, t) {
            html += '<div class="col-sm-6 col-xl-3"><div class="rc-tile">'
                 + '<div class="rc-tile-label">' + R.escapeHtml(t.label) + "</div>"
                 + '<div class="rc-tile-value">' + t.value + "</div>"
                 + '<div class="rc-tile-sub">' + R.escapeHtml(t.sub) + "</div>"
                 + "</div></div>";
        });
        $("#ex-tiles").html(html);
    }

    function renderRows(rows) {
        if (rows.length === 0) {
            $("#ex-rows").html('<tr><td colspan="10" class="rc-empty">No exceptions match these filters.</td></tr>');
            $("#ex-count").text("");
            return;
        }
        var html = "";
        $.each(rows, function (_, e) {
            var a = ageDays(e.businessDate);
            var sev = severityFor(e.code);
            html += "<tr>"
                 + '<td class="rc-mono">' + e.id + "</td>"
                 + '<td class="rc-mono">' + R.escapeHtml(e.ref) + "</td>"
                 + "<td><code class='rc-field'>" + R.escapeHtml(e.code) + "</code>"
                 + '<div class="rc-help">' + R.escapeHtml(labelFor(e.code)) + "</div></td>"
                 + "<td>" + R.escapeHtml(e.side) + "</td>"
                 + '<td class="rc-mono">' + e.businessDate + "</td>"
                 + '<td class="rc-num' + (a > 7 ? " rc-num-neg fw-semibold" : "") + '">'
                 + a + "d</td>"
                 + '<td class="rc-num">' + R.formatMinor(e.amountMinor, e.currency)
                 + ' <span class="rc-help">' + R.escapeHtml(e.currency) + "</span></td>"
                 + "<td>" + R.statusBadge(e.status)
                 + (sev === "Critical" ? ' <span class="badge text-bg-danger">critical</span>' : "")
                 + "</td>"
                 + "<td>" + (e.assignedTo ? R.escapeHtml(e.assignedTo) : '<span class="text-muted">unassigned</span>') + "</td>"
                 + '<td class="rc-mono">' + e.runId + "</td>"
                 + "</tr>";
        });
        $("#ex-rows").html(html);
        $("#ex-count").text(rows.length + " item" + (rows.length === 1 ? "" : "s"));
    }

    function render() {
        var rows = filtered();
        renderTiles(rows);
        renderRows(rows);
    }

    $(function () {
        R.renderShell({ title: "Exceptions" });

        var opts = '<option value="">All</option>';
        $.each(M.exceptionCodes, function (_, c) {
            opts += '<option value="' + c.code + '">' + R.escapeHtml(c.code) + "</option>";
        });
        $("#f-code").html(opts);

        $("#f-code, #f-exstatus, #f-age").on("change", render);
        $("#f-search").on("input", render);
        $("#btn-export").on("click", function () {
            window.alert("Prototype: the export would be recorded in aud.AuditLog with "
                       + "Action = 'Export' — who downloaded which report, and when. "
                       + "Regulators ask, and v0.2 could not answer.");
        });

        render();
    });
}(jQuery));
