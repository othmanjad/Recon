/* =====================================================================
   The rule builder's client side.

   What it does NOT do is the important part: it never sends a field name
   anywhere except as a code the server resolves against the registry
   again. The dropdowns are built from the registry the server rendered,
   and a field the registry does not mark matchable is not in them.

   What it does:
     * adds and removes condition rows, keeping the six parallel arrays
       aligned (one value per row per array, always — that is why the
       normalized flag is a select and not a checkbox);
     * warns about a comparison that cannot seek an index, and warns
       harder when it is in pass 1, which runs against the full dataset;
     * warns when a tolerance comparison has no tolerance, which the
       server refuses;
     * validates the condition tree against the server.
   ===================================================================== */

jQuery(function ($) {
    "use strict";

    var config = window.ReconRules;
    if (!config) { return; }

    /* Comparisons that cannot use an index. Kept in step with
       ComparisonTypeExtensions.Indexability in the domain: Contains and
       EndsWith scan, StartsWith seeks a prefix. */
    var SCANS = { Contains: true, EndsWith: true };
    var PARTIAL = { StartsWith: true };
    var NEEDS_TOLERANCE = { NumericTolerance: true, DateWithin: true };

    function options(fields, selected) {
        var html = "";

        $.each(fields, function (_, f) {
            if (!f.matchable) { return; }

            html += '<option value="' + f.code + '"'
                + (f.code === selected ? " selected" : "")
                + ' data-type="' + f.type + '"'
                + ' data-normalize="' + (f.normalize ? "true" : "false") + '"'
                + ' data-indexed="' + (f.indexed ? "true" : "false") + '">'
                + f.label + " (" + f.code + ")</option>";
        });

        return html;
    }

    function comparisonOptions() {
        var html = "";
        $.each(config.comparisons, function (_, c) {
            html += "<option value=\"" + c + "\">" + c + "</option>";
        });
        return html;
    }

    function unitOptions() {
        var html = '<option value="">(unit)</option>';
        $.each(config.units, function (_, u) {
            html += "<option value=\"" + u + "\">" + u + "</option>";
        });
        return html;
    }

    function row() {
        return $(
            '<div class="rc-cond-row">' +
            '  <div class="row g-2 align-items-center">' +
            '    <div class="col-md-3"><select class="form-select form-select-sm rc-left" name="leftFieldCodes" aria-label="Left field">' + options(config.left.fields) + '</select></div>' +
            '    <div class="col-md-2"><select class="form-select form-select-sm rc-cmp" name="comparisons" aria-label="Comparison">' + comparisonOptions() + '</select></div>' +
            '    <div class="col-md-3"><select class="form-select form-select-sm rc-right" name="rightFieldCodes" aria-label="Right field">' + options(config.right.fields) + '</select></div>' +
            '    <div class="col-md-1"><input class="form-control form-control-sm rc-tol" type="number" name="tolerances" placeholder="tol" aria-label="Tolerance"></div>' +
            '    <div class="col-md-2"><select class="form-select form-select-sm rc-unit" name="toleranceUnits" aria-label="Tolerance unit">' + unitOptions() + '</select></div>' +
            '    <div class="col-md-1"><select class="form-select form-select-sm rc-norm" name="useNormalized" aria-label="Compare normalized companions">' +
            '      <option value="false">raw</option><option value="true">norm</option></select></div>' +
            '  </div>' +
            '  <div class="rc-help mt-1 rc-cond-note"></div>' +
            '  <div class="mt-1"><button class="btn btn-sm btn-link text-danger p-0 rc-remove-cond" type="button">remove</button></div>' +
            '</div>');
    }

    /* ---- per-row advice -------------------------------------------- */
    function annotate($row) {
        var $pass = $row.closest("form");
        var sequence = parseInt($pass.find('input[name="sequence"]').val(), 10) || 0;

        var comparison = $row.find(".rc-cmp").val();
        var $left = $row.find(".rc-left option:selected");
        var $right = $row.find(".rc-right option:selected");
        var normalized = $row.find(".rc-norm").val() === "true";
        var tolerance = $row.find(".rc-tol").val();
        var unit = $row.find(".rc-unit").val();

        var notes = [];
        var warn = false;

        if (SCANS[comparison]) {
            warn = true;
            notes.push(comparison + " cannot use an index"
                + (sequence === 1
                    ? " — and this is pass 1, which runs against the full dataset on both sides."
                    : "; keep it out of the early passes."));
        } else if (PARTIAL[comparison]) {
            notes.push("StartsWith can seek a prefix, so it is cheaper than Contains but not free.");
        }

        if (NEEDS_TOLERANCE[comparison] && (!tolerance || !unit)) {
            warn = true;
            notes.push(comparison + " needs both a tolerance value and a unit; without them the "
                + "server refuses the pass rather than treating it as an exact match.");
        }

        if (!NEEDS_TOLERANCE[comparison] && (tolerance || unit)) {
            notes.push("A tolerance on " + comparison + " is ignored.");
        }

        if (normalized) {
            var leftNorm = $left.data("normalize") === true || $left.data("normalize") === "true";
            var rightNorm = $right.data("normalize") === true || $right.data("normalize") === "true";

            if (!leftNorm || !rightNorm) {
                warn = true;
                notes.push("Both fields need a normalized companion for this: comparing a "
                    + "normalized value against a raw one matches nothing.");
            } else {
                notes.push("Compares the companions filled at load time — still an indexed "
                    + "Exact match, not a runtime UPPER(TRIM(...)).");
            }
        }

        /* Comparing two fields of different declared types is not refused,
           but it is nearly always a mapping mistake worth saying out loud. */
        var leftType = $left.data("type");
        var rightType = $right.data("type");

        if (leftType && rightType && leftType !== rightType) {
            notes.push("These fields are declared " + leftType + " and " + rightType + ".");
        }

        $row.toggleClass("rc-warn-nonsargable", warn);
        $row.find(".rc-cond-note").text(notes.join(" "));
    }

    function annotateAll() {
        $(".rc-cond-row").each(function () { annotate($(this)); });
    }

    $(document).on("click", ".rc-add-cond", function () {
        var $conditions = $(this).closest("form").find(".rc-conditions");
        var $new = row();
        $conditions.append($new);
        annotate($new);
    });

    $(document).on("click", ".rc-remove-cond", function () {
        $(this).closest(".rc-cond-row").remove();
    });

    $(document).on("change keyup", ".rc-cond-row select, .rc-cond-row input", function () {
        annotate($(this).closest(".rc-cond-row"));
    });

    /* A pass with no condition would match every row against every row, so
       the button says so before the server does. */
    $(document).on("submit", "form", function () {
        var $conditions = $(this).find(".rc-conditions");
        if ($conditions.length && $conditions.find(".rc-cond-row").length === 0) {
            window.alert("A pass needs at least one condition: with none it would match every row "
                + "against every row.");
            return false;
        }
        return true;
    });

    /* ---- the condition tree ---------------------------------------- */
    var VALID_EXAMPLE = null;

    (function buildExamples() {
        var matchable = $.grep(config.left.fields, function (f) { return f.matchable; });
        var withheld = $.grep(config.left.fields, function (f) { return !f.matchable; });

        if (matchable.length > 0) {
            VALID_EXAMPLE = {
                op: "and",
                items: [{ field: matchable[0].code, cmp: "isnotnull" }]
            };

            if (matchable.length > 1) {
                VALID_EXAMPLE.items.push({
                    op: "or",
                    items: [
                        { field: matchable[1].code, cmp: "eq", value: "ACSC" },
                        { field: matchable[1].code, cmp: "eq", value: "ACCC" }
                    ]
                });
            }
        }

        window.ReconRules.badExample = {
            op: "and",
            items: [{
                field: withheld.length > 0 ? withheld[0].code : "NO_SUCH_FIELD",
                cmp: "eq",
                value: "x"
            }]
        };
    }());

    $("#btn-sample-ok").on("click", function () {
        $("#ct-json").val(VALID_EXAMPLE ? JSON.stringify(VALID_EXAMPLE, null, 2) : "{}");
    });

    $("#btn-sample-bad").on("click", function () {
        $("#ct-json").val(JSON.stringify(window.ReconRules.badExample, null, 2));
    });

    $("#btn-validate").on("click", function () {
        var json = $("#ct-json").val();
        var $result = $("#ct-result");

        /* Checked here first for an instant answer, then on the server,
           which is the boundary. The client copy exists to save a round
           trip, never to be trusted. */
        var local = window.ReconConditionTree.parseAndValidate(json, {
            code: config.left.code,
            fields: config.left.fields
        });

        if (!local.valid) {
            $result.html(render(false, local.errors, "checked here, not yet sent"));
            return;
        }

        $result.html('<div class="rc-help">Validating on the server…</div>');

        $.ajax({
            url: config.validateUrl,
            method: "POST",
            data: { definitionId: config.definitionId, side: "Left", json: json },
            headers: { RequestVerificationToken: config.token }
        }).done(function (response) {
            if (response.valid) {
                $result.html(
                    '<div class="alert alert-success mb-0 small"><strong>Accepted</strong> against '
                    + response.dataset + "'s registry.<div class=\"mt-1\">"
                    + escapeHtml(window.ReconConditionTree.describe(local.tree, {
                        code: config.left.code, fields: config.left.fields
                    })) + "</div></div>");
            } else {
                $result.html(render(false, response.errors, "refused by the server"));
            }
        }).fail(function () {
            $result.html(render(false, ["the validation request failed"], "server"));
        });
    });

    function render(ok, errors, where) {
        var html = '<div class="alert alert-danger mb-0 small"><strong>Rejected</strong> ('
            + escapeHtml(where) + ")<ul class=\"mb-0 mt-1\">";

        $.each(errors, function (_, e) { html += "<li>" + escapeHtml(e) + "</li>"; });
        return html + "</ul></div>";
    }

    function escapeHtml(text) {
        return $("<div>").text(text === null || text === undefined ? "" : text).html();
    }

    annotateAll();
});
