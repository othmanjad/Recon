/* Rule builder — design §9 and the safety property of §6.4.
   Field dropdowns are populated from the two datasets' field registries.
   Nothing here accepts a typed field name. */
(function ($) {
    "use strict";

    var M = window.ReconMock, R = window.Recon, CT = window.ReconConditionTree;
    var defId = 1;
    var passes = [];

    function leftDataset()  { var d = M.definitionById(defId); return d ? M.datasetById(d.leftDatasetId) : null; }
    function rightDataset() { var d = M.definitionById(defId); return d ? M.datasetById(d.rightDatasetId) : null; }

    function cloneRules() {
        return $.map(M.rules, function (r) {
            return {
                seq: r.seq, code: r.code, name: r.name, mode: r.mode,
                cardinality: r.cardinality, onMultiple: r.onMultiple,
                conditions: $.map(r.conditions, function (c) {
                    return { leftFieldId: c.leftFieldId, rightFieldId: c.rightFieldId,
                             cmp: c.cmp, tolerance: c.tolerance, unit: c.unit,
                             useNormalized: c.useNormalized };
                })
            };
        });
    }

    function cmpMeta(value) {
        var out = null;
        $.each(M.comparisons, function (_, c) { if (c.value === value) { out = c; } });
        return out;
    }

    function cmpOptions(selected) {
        var html = "";
        $.each(M.comparisons, function (_, c) {
            html += '<option value="' + c.value + '"' + (c.value === selected ? " selected" : "") + ">"
                 + R.escapeHtml(c.label) + "</option>";
        });
        return html;
    }

    function renderPasses() {
        var L = leftDataset(), Rt = rightDataset();
        var html = "";

        $.each(passes, function (pi, p) {
            html += '<div class="rc-pass" data-pass="' + pi + '">'
                 + '<div class="rc-pass-head">'
                 + '<span class="rc-seq">' + p.seq + "</span>"
                 + '<input class="form-control form-control-sm p-name" style="max-width:320px"'
                 + ' value="' + R.escapeHtml(p.name) + '" aria-label="Pass name">'
                 + '<code class="rc-field">' + R.escapeHtml(p.code) + "</code>"
                 + '<select class="form-select form-select-sm p-mode" style="width:auto" aria-label="Match mode">'
                 + '<option value="Row"' + (p.mode === "Row" ? " selected" : "") + ">Row ↔ row</option>"
                 + '<option value="Aggregate"' + (p.mode === "Aggregate" ? " selected" : "") + ">Aggregate (summary ↔ many)</option>"
                 + "</select>"
                 + '<select class="form-select form-select-sm p-multi" style="width:auto" aria-label="On multiple match">'
                 + '<option value="MarkAmbiguous"' + (p.onMultiple === "MarkAmbiguous" ? " selected" : "") + ">On multiple: mark ambiguous</option>"
                 + '<option value="TakeEarliest"' + (p.onMultiple === "TakeEarliest" ? " selected" : "") + ">On multiple: take earliest</option>"
                 + '<option value="Fail"' + (p.onMultiple === "Fail" ? " selected" : "") + ">On multiple: fail the run</option>"
                 + "</select>"
                 + '<div class="ms-auto d-flex gap-1">'
                 + '<button class="btn btn-sm btn-outline-secondary btn-add-cond" type="button" title="Add condition">'
                 + '<i class="bi bi-plus-lg"></i></button>'
                 + '<button class="btn btn-sm btn-outline-danger btn-del-pass" type="button" title="Remove pass">'
                 + '<i class="bi bi-trash"></i></button>'
                 + "</div></div>";

            if (p.conditions.length === 0) {
                html += '<div class="rc-empty">No conditions. A pass with no condition matches nothing.</div>';
            }

            $.each(p.conditions, function (ci, c) {
                var meta = cmpMeta(c.cmp);
                var nonSargable = meta && meta.indexable === "no";
                var lf = M.fieldById(c.leftFieldId), rf = M.fieldById(c.rightFieldId);

                html += '<div class="rc-cond-row' + (nonSargable ? " rc-warn-nonsargable" : "")
                     + '" data-cond="' + ci + '">'
                     + '<div class="row g-2 align-items-end">'
                     + '<div class="col-md-4"><label class="form-label rc-help mb-1">Left · '
                     + R.escapeHtml(L ? L.code : "—") + "</label>"
                     + '<select class="form-select form-select-sm c-left">'
                     + R.fieldOptions(L, c.leftFieldId) + "</select></div>"
                     + '<div class="col-md-4"><label class="form-label rc-help mb-1">Comparison</label>'
                     + '<select class="form-select form-select-sm c-cmp">' + cmpOptions(c.cmp) + "</select></div>"
                     + '<div class="col-md-4"><label class="form-label rc-help mb-1">Right · '
                     + R.escapeHtml(Rt ? Rt.code : "—") + "</label>"
                     + '<select class="form-select form-select-sm c-right">'
                     + R.fieldOptions(Rt, c.rightFieldId) + "</select></div>"
                     + "</div>";

                /* tolerance inputs only where the comparison takes one */
                if (meta && meta.needsTolerance) {
                    html += '<div class="row g-2 mt-2 align-items-end">'
                         + '<div class="col-6 col-md-3"><label class="form-label rc-help mb-1">Tolerance</label>'
                         + '<input type="number" min="0" class="form-control form-control-sm c-tol" value="'
                         + (c.tolerance === undefined ? "" : c.tolerance) + '"></div>'
                         + '<div class="col-6 col-md-3"><label class="form-label rc-help mb-1">Unit</label>'
                         + '<select class="form-select form-select-sm c-unit">'
                         + $.map(["MinorUnit", "Minute", "Hour", "Day"], function (u) {
                               return '<option value="' + u + '"' + (c.unit === u ? " selected" : "") + ">" + u + "</option>";
                           }).join("")
                         + "</select></div></div>";
                }

                /* the normalized-companion switch: only offered for a string
                   field that actually HAS a companion slot reserved. */
                if (lf && lf.normalize && rf && rf.normalize) {
                    html += '<div class="form-check form-switch mt-2">'
                         + '<input class="form-check-input c-norm" type="checkbox" id="norm-' + pi + "-" + ci + '"'
                         + (c.useNormalized ? " checked" : "") + ">"
                         + '<label class="form-check-label rc-help" for="norm-' + pi + "-" + ci + '">'
                         + "Compare the normalized companions (<span class='rc-mono'>"
                         + R.escapeHtml(lf.normalizedSlot) + " ↔ " + R.escapeHtml(rf.normalizedSlot)
                         + "</span>) — filled at parse time, so this stays an indexed "
                         + "<code>Exact</code> match rather than a scan.</label></div>";
                }

                if (nonSargable) {
                    html += '<div class="small mt-2 mb-0 d-flex gap-2 align-items-start">'
                         + '<i class="bi bi-exclamation-triangle-fill text-warning"></i>'
                         + "<span><strong>Not indexable.</strong> "
                         + R.escapeHtml(c.cmp) + " cannot use an index and will scan both sides. "
                         + "Keep it in a late pass over a small remainder — never in pass 1 or 2.</span></div>";
                } else if (meta && meta.indexable === "partly") {
                    html += '<div class="small rc-help mt-2 mb-0">'
                         + "Partly indexable: a prefix match can seek, but only if the field is indexed.</div>";
                }

                html += '<div class="text-end mt-2">'
                     + '<button class="btn btn-sm btn-link text-danger btn-del-cond p-0" type="button">remove condition</button>'
                     + "</div></div>";
            });

            html += "</div>";
        });

        $("#passes").html(html);
        renderOrderWarning();
    }

    /* The engineering rule the UI owes the user: non-indexable comparisons
       belong late. Say it about the rule set as a whole, not just per row. */
    function renderOrderWarning() {
        var bad = [];
        $.each(passes, function (_, p) {
            if (p.seq > 2) { return; }
            $.each(p.conditions, function (__, c) {
                var meta = cmpMeta(c.cmp);
                if (meta && meta.indexable !== "yes") { bad.push(p.seq + " (" + p.code + ")"); }
            });
        });
        $("#order-warning").remove();
        if (bad.length > 0) {
            $("#passes").before('<div class="alert alert-warning small" id="order-warning">'
                + '<i class="bi bi-exclamation-triangle"></i> <strong>Pass order problem.</strong> '
                + "Pass " + bad.join(", ") + " uses a comparison that cannot seek an index, "
                + "in the pass that runs against the full 2M rows. Move it after the "
                + "reference passes.</div>");
        }
    }

    function renderDefinition() {
        var d = M.definitionById(defId);
        var L = leftDataset(), Rt = rightDataset();
        $("#def-sides").html(d
            ? "<div><strong>Left</strong> <code class='rc-field'>" + R.escapeHtml(L ? L.code : "—")
              + "</code> &nbsp;↔&nbsp; <strong>Right</strong> <code class='rc-field'>"
              + R.escapeHtml(Rt ? Rt.code : "—") + "</code></div>"
              + "<div class='mt-1'>Matching window: −" + d.windowBefore + " / +" + d.windowAfter
              + " day(s) around the business date. Late arrivals mean the window is never "
              + "simply &quot;= business date&quot;.</div>"
            : "");
    }

    /* ---- condition-tree panel (D1 / B5) ---- */

    var SAMPLE_OK = {
        op: "and",
        items: [
            { field: "STATUS", cmp: "ne", value: "RJCT" },
            { op: "or", items: [
                { field: "DIRECTION", cmp: "eq", value: "Inward" },
                { field: "AMOUNT", cmp: "gte", value: 1000 }
            ] }
        ]
    };

    /* Two rejections in one payload: a field that is not in the registry,
       and a field that is in it but not marked matchable. */
    var SAMPLE_BAD = {
        op: "and",
        items: [
            { field: "STATUS'; DROP TABLE stg.StagingTransaction--", cmp: "eq", value: "x" },
            { field: "CURRENCY", cmp: "eq", value: "JOD" },
            { field: "AMOUNT", cmp: "in", value: [] }
        ]
    };

    function validateTree() {
        var raw = $("#ct-json").val();
        var res = CT.parseAndValidate(raw, leftDataset());
        var html;
        if (res.valid) {
            html = '<div class="alert alert-success py-2 px-3 small mb-2">'
                 + '<i class="bi bi-check-circle"></i> <strong>Valid.</strong> '
                 + "Every field resolved against the registry of "
                 + R.escapeHtml(leftDataset().code) + "."
                 + "</div>"
                 + '<div class="rc-card"><div class="rc-card-head"><h3>Reads as</h3></div>'
                 + '<div class="p-3 small">' + R.escapeHtml(CT.describe(res.tree, leftDataset()))
                 + "</div></div>";
        } else {
            html = '<div class="alert alert-danger py-2 px-3 small mb-2">'
                 + '<i class="bi bi-x-octagon"></i> <strong>Rejected — '
                 + res.errors.length + " problem" + (res.errors.length === 1 ? "" : "s")
                 + ".</strong> Nothing is compiled and nothing reaches SQL.</div>"
                 + '<ul class="small ps-3 mb-0">';
            $.each(res.errors, function (_, e) {
                html += "<li class='mb-1'>" + R.escapeHtml(e) + "</li>";
            });
            html += "</ul>";
        }
        $("#ct-result").html(html);
    }

    function renderDryRun() {
        var run = M.runById(4471);
        var total = 0;
        $.each(run.passes, function (_, p) { total += p.matched; });
        var html = '<div class="rc-table-scroll"><table class="table rc-table align-middle"><thead><tr>'
                 + "<th>Pass</th><th>Rule</th><th class='text-end'>Rows in</th>"
                 + "<th class='text-end'>Matched</th><th class='text-end'>Match rate</th>"
                 + "</tr></thead><tbody>";
        $.each(run.passes, function (_, p) {
            var rate = p.rows > 0 ? (100 * p.matched / p.rows) : 0;
            html += "<tr><td><span class='rc-seq'>" + p.seq + "</span></td>"
                 + "<td class='rc-mono'>" + R.escapeHtml(p.code) + "</td>"
                 + "<td class='rc-num'>" + R.formatCount(p.rows) + "</td>"
                 + "<td class='rc-num'>" + R.formatCount(p.matched) + "</td>"
                 + "<td class='rc-num'>" + rate.toFixed(2) + "%</td></tr>";
        });
        html += "</tbody></table></div>"
             + "<div class='rc-help mt-2'>Sample of 2 000 000 rows · "
             + R.formatCount(total) + " matched overall · sandbox run, excluded from aggregates "
             + "and purged after 7 days.</div>";
        $("#dryrun-body").html(html);
        $("#dryrun-card").prop("hidden", false);
    }

    $(function () {
        R.renderShell({ title: "Rule builder" });

        var opts = "";
        $.each(M.definitions, function (_, d) {
            opts += '<option value="' + d.id + '">' + R.escapeHtml(d.name)
                 + " (" + R.escapeHtml(d.code) + ")</option>";
        });
        $("#def-select").html(opts).val(defId);

        passes = cloneRules();
        renderDefinition();
        renderPasses();
        $("#ct-json").val(JSON.stringify(SAMPLE_OK, null, 2));

        $("#def-select").on("change", function () {
            defId = parseInt($(this).val(), 10);
            renderDefinition();
            renderPasses();
            $("#ct-result").html('<div class="rc-help">Not validated yet.</div>');
        });

        /* delegated handlers — the DOM is re-rendered on every change */
        $("#passes")
            .on("change", ".c-cmp", function () {
                var $row = $(this).closest(".rc-cond-row"), $p = $(this).closest(".rc-pass");
                passes[$p.data("pass")].conditions[$row.data("cond")].cmp = $(this).val();
                renderPasses();
            })
            .on("change", ".c-left", function () {
                var $row = $(this).closest(".rc-cond-row"), $p = $(this).closest(".rc-pass");
                passes[$p.data("pass")].conditions[$row.data("cond")].leftFieldId =
                    parseInt($(this).val(), 10) || null;
                renderPasses();
            })
            .on("change", ".c-right", function () {
                var $row = $(this).closest(".rc-cond-row"), $p = $(this).closest(".rc-pass");
                passes[$p.data("pass")].conditions[$row.data("cond")].rightFieldId =
                    parseInt($(this).val(), 10) || null;
                renderPasses();
            })
            .on("change", ".c-norm", function () {
                var $row = $(this).closest(".rc-cond-row"), $p = $(this).closest(".rc-pass");
                passes[$p.data("pass")].conditions[$row.data("cond")].useNormalized = $(this).is(":checked");
            })
            .on("change", ".c-tol", function () {
                var $row = $(this).closest(".rc-cond-row"), $p = $(this).closest(".rc-pass");
                passes[$p.data("pass")].conditions[$row.data("cond")].tolerance =
                    parseInt($(this).val(), 10);
            })
            .on("change", ".c-unit", function () {
                var $row = $(this).closest(".rc-cond-row"), $p = $(this).closest(".rc-pass");
                passes[$p.data("pass")].conditions[$row.data("cond")].unit = $(this).val();
            })
            .on("change", ".p-mode", function () {
                passes[$(this).closest(".rc-pass").data("pass")].mode = $(this).val();
                renderPasses();
            })
            .on("change", ".p-multi", function () {
                passes[$(this).closest(".rc-pass").data("pass")].onMultiple = $(this).val();
            })
            .on("input", ".p-name", function () {
                passes[$(this).closest(".rc-pass").data("pass")].name = $(this).val();
            })
            .on("click", ".btn-add-cond", function () {
                passes[$(this).closest(".rc-pass").data("pass")].conditions.push(
                    { leftFieldId: null, rightFieldId: null, cmp: "Exact" });
                renderPasses();
            })
            .on("click", ".btn-del-cond", function () {
                var $row = $(this).closest(".rc-cond-row"), $p = $(this).closest(".rc-pass");
                passes[$p.data("pass")].conditions.splice($row.data("cond"), 1);
                renderPasses();
            })
            .on("click", ".btn-del-pass", function () {
                passes.splice($(this).closest(".rc-pass").data("pass"), 1);
                $.each(passes, function (i, p) { p.seq = i + 1; });
                renderPasses();
            });

        $("#btn-add-pass").on("click", function () {
            var seq = passes.length + 1;
            passes.push({ seq: seq, code: "P" + seq + "_NEW", name: "New pass " + seq,
                          mode: "Row", cardinality: "OneToOne", onMultiple: "MarkAmbiguous",
                          conditions: [] });
            renderPasses();
        });

        $("#btn-validate").on("click", validateTree);
        $("#btn-sample-ok").on("click", function () {
            $("#ct-json").val(JSON.stringify(SAMPLE_OK, null, 2));
            validateTree();
        });
        $("#btn-sample-bad").on("click", function () {
            $("#ct-json").val(JSON.stringify(SAMPLE_BAD, null, 2));
            validateTree();
        });
        $("#btn-dryrun").on("click", renderDryRun);
        $("#btn-save").on("click", function () {
            validateTree();
            window.alert("Prototype: the rule set would be saved and versioned here, "
                       + "and a definition snapshot taken at the next run start.");
        });
    });
}(jQuery));
