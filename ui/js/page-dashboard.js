/* Run dashboard — design §13 "Operations": status, duration and the
   match distribution per pass. */
(function ($) {
    "use strict";

    var M = window.ReconMock, R = window.Recon, CT = window.ReconConditionTree;
    var selectedRunId = 4471;

    function filtered() {
        var defId = $("#f-definition").val();
        var status = $("#f-status").val();
        return $.grep(M.runs, function (r) {
            if (defId && String(r.definitionId) !== String(defId)) { return false; }
            if (status && r.status !== status) { return false; }
            return true;
        });
    }

    function renderTiles(runs) {
        var current = $.grep(runs, function (r) { return r.isCurrent; });
        var totalMatched = 0, totalUnmatched = 0, totalAmbiguous = 0, failed = 0, running = 0;
        $.each(current, function (_, r) {
            totalMatched += r.matchedCnt || 0;
            totalUnmatched += r.unmatchedCnt || 0;
            totalAmbiguous += r.ambiguousCnt || 0;
            if (r.status === "Failed") { failed++; }
            if (r.status === "Running") { running++; }
        });
        var rate = (totalMatched + totalUnmatched) > 0
            ? (100 * totalMatched / (totalMatched + totalUnmatched)) : 0;

        var tiles = [
            { label: "Current runs", value: R.formatCount(current.length),
              sub: running + " running · " + failed + " failed" },
            { label: "Matched rows", value: R.formatCount(totalMatched),
              sub: rate.toFixed(3) + "% of matchable" },
            { label: "Unmatched", value: R.formatCount(totalUnmatched),
              sub: "become classified exceptions" },
            { label: "Ambiguous", value: R.formatCount(totalAmbiguous),
              sub: "never auto-resolved — sent to Operations" }
        ];

        var html = "";
        $.each(tiles, function (_, t) {
            html += '<div class="col-sm-6 col-xl-3"><div class="rc-tile">'
                 + '<div class="rc-tile-label">' + R.escapeHtml(t.label) + "</div>"
                 + '<div class="rc-tile-value">' + t.value + "</div>"
                 + '<div class="rc-tile-sub">' + R.escapeHtml(t.sub) + "</div>"
                 + "</div></div>";
        });
        $("#tiles").html(html);
    }

    function renderRuns(runs) {
        if (runs.length === 0) {
            $("#run-rows").html('<tr><td colspan="12" class="rc-empty">No runs match these filters.</td></tr>');
            $("#run-count").text("");
            return;
        }
        var html = "";
        $.each(runs, function (_, r) {
            var def = M.definitionById(r.definitionId);
            var superseded = !r.isCurrent;
            html += '<tr class="rc-run-row' + (superseded ? " text-muted" : "")
                 + '" data-run="' + r.id + '">'
                 + '<td class="rc-mono">' + r.id
                 + (superseded ? ' <span class="badge text-bg-secondary">superseded</span>' : "")
                 + "</td>"
                 + "<td>" + R.escapeHtml(def ? def.name : "—") + "</td>"
                 + '<td class="rc-mono">' + r.businessDate + "</td>"
                 + "<td>" + (r.sessionRef ? R.escapeHtml(r.sessionRef) : '<span class="text-muted">—</span>') + "</td>"
                 + "<td>" + R.runTypeBadge(r.type)
                 + (r.sourceRunId ? ' <span class="rc-help">from ' + r.sourceRunId + "</span>" : "")
                 + "</td>"
                 + "<td>" + R.statusBadge(r.status) + "</td>"
                 + '<td class="rc-num">' + R.formatCount(r.leftRowCnt) + "</td>"
                 + '<td class="rc-num">' + R.formatCount(r.rightRowCnt) + "</td>"
                 + '<td class="rc-num">' + R.formatCount(r.matchedCnt) + "</td>"
                 + '<td class="rc-num">' + R.formatCount(r.unmatchedCnt) + "</td>"
                 + '<td class="rc-num">' + R.formatDuration(r.durationSec) + "</td>"
                 + '<td class="text-end"><button class="btn btn-sm btn-outline-secondary btn-view"'
                 + ' data-run="' + r.id + '" type="button">View</button></td>'
                 + "</tr>";
            if (r.error) {
                html += '<tr class="' + (superseded ? "text-muted" : "") + '">'
                     + '<td colspan="12" class="pt-0">'
                     + '<div class="alert alert-danger py-2 px-3 mb-0 small">'
                     + '<i class="bi bi-exclamation-octagon"></i> '
                     + R.escapeHtml(r.error) + "</div></td></tr>";
            }
        });
        $("#run-rows").html(html);
        $("#run-count").text(runs.length + " run" + (runs.length === 1 ? "" : "s"));
    }

    function renderDistribution(runId) {
        var run = M.runById(runId);
        if (!run || !run.passes || run.passes.length === 0) {
            $("#dist").html('<div class="rc-empty">This run recorded no pass detail.</div>');
            $("#dist-run").text(run ? "· run " + run.id : "");
            return;
        }
        var totalMatched = 0;
        $.each(run.passes, function (_, p) { totalMatched += p.matched; });

        var html = '<div class="rc-table-scroll"><table class="table rc-table align-middle"><thead><tr>'
                 + "<th>Pass</th><th>Rule</th><th>Share</th>"
                 + '<th class="text-end">Rows in</th><th class="text-end">Matched</th>'
                 + '<th class="text-end">Time</th></tr></thead><tbody>';
        $.each(run.passes, function (i, p) {
            var share = totalMatched > 0 ? (100 * p.matched / totalMatched) : 0;
            var isLate = (i === run.passes.length - 1) && share > 15;
            html += "<tr>"
                 + '<td><span class="rc-seq">' + p.seq + "</span></td>"
                 + '<td class="rc-mono">' + R.escapeHtml(p.code) + "</td>"
                 + '<td style="min-width:160px">'
                 + '<div class="rc-pass-bar' + (isLate ? " rc-pass-late" : "") + '">'
                 + '<span style="width:' + share.toFixed(2) + '%"></span></div>'
                 + '<div class="rc-help">' + share.toFixed(2) + "%</div></td>"
                 + '<td class="rc-num">' + R.formatCount(p.rows) + "</td>"
                 + '<td class="rc-num">' + R.formatCount(p.matched) + "</td>"
                 + '<td class="rc-num">' + (p.ms / 1000).toFixed(1) + "s</td>"
                 + "</tr>";
        });
        html += "</tbody></table></div>";

        var lastShare = totalMatched > 0
            ? (100 * run.passes[run.passes.length - 1].matched / totalMatched) : 0;
        if (lastShare > 15) {
            html += '<div class="alert alert-warning py-2 px-3 mt-2 mb-0 small">'
                 + "<strong>MatchDistributionDrift.</strong> The last pass carried "
                 + lastShare.toFixed(2) + "% of matches, above the 15% threshold. "
                 + "The composite fallback is doing work the reference match should be doing."
                 + "</div>";
        }
        $("#dist").html(html);
        $("#dist-run").text("· run " + run.id);
    }

    function renderTotals(runId) {
        var run = M.runById(runId);
        if (!run || !run.controlTotals || run.controlTotals.length === 0) {
            $("#totals").html('<div class="rc-empty">No control totals recorded for this run.</div>');
            return;
        }
        var html = '<div class="rc-table-scroll"><table class="table rc-table align-middle"><thead><tr>'
                 + "<th>Check</th>"
                 + '<th class="text-end">Difference</th><th></th>'
                 + '<th class="text-end">Source A</th><th class="text-end">Source B</th>'
                 + "</tr></thead><tbody>";
        $.each(run.controlTotals, function (_, c) {
            var diff = c.a - c.b;
            var isCount = c.code.indexOf("COUNT") !== -1;
            var fmt = function (v) { return isCount ? R.formatCount(v) : R.formatMinor(v, run.currency); };
            /* Difference and its verdict come first: a non-zero net
               difference is what fails the run, so it must never be the
               column that scrolled off the side of the panel. */
            html += "<tr>"
                 + "<td>" + R.escapeHtml(c.name) + '<div class="rc-help rc-mono">' + R.escapeHtml(c.code) + "</div></td>"
                 + '<td class="rc-num' + (diff !== 0 ? " rc-num-neg fw-semibold" : "") + '">'
                 + (diff === 0 ? "0" : fmt(diff)) + "</td>"
                 + "<td>"
                 + (c.balanced
                     ? '<span class="badge text-bg-success">balanced</span>'
                     : '<span class="badge text-bg-danger">mismatch</span>')
                 + "</td>"
                 + '<td class="rc-num">' + fmt(c.a) + "</td>"
                 + '<td class="rc-num">' + fmt(c.b) + "</td>"
                 + "</tr>";
        });
        html += "</tbody></table></div>";
        $("#totals").html(html);
    }

    function render() {
        var runs = filtered();
        renderTiles(runs);
        renderRuns(runs);
        renderDistribution(selectedRunId);
        renderTotals(selectedRunId);
        $(".rc-run-row").removeClass("table-active")
            .filter('[data-run="' + selectedRunId + '"]').addClass("table-active");
    }

    $(function () {
        R.renderShell({ title: "Run dashboard" });

        var opts = '<option value="">All definitions</option>';
        $.each(M.definitions, function (_, d) {
            opts += '<option value="' + d.id + '">' + R.escapeHtml(d.name) + "</option>";
        });
        $("#f-definition").html(opts);

        $("#f-definition, #f-status").on("change", render);
        $("#btn-reset").on("click", function () {
            $("#f-definition, #f-status").val("");
            render();
        });
        $("#run-rows").on("click", ".btn-view, .rc-run-row", function (e) {
            var id = parseInt($(e.currentTarget).data("run"), 10);
            if (!isNaN(id)) { selectedRunId = id; render(); }
        });

        render();
    });
}(jQuery));
