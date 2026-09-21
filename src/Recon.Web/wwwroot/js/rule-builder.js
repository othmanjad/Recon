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

    /* The server's own wording for these two, handed over in config.words:
       a screen that shows an operator "MinorUnit" is a screen that assumes
       they read the schema. */
    function word(kind, value) {
        var table = (config.words && config.words[kind]) || {};
        return table[value] || value;
    }

    function comparisonOptions() {
        var html = "";
        $.each(config.comparisons, function (_, c) {
            html += '<option value="' + c + '">' + word("comparisons", c) + "</option>";
        });
        return html;
    }

    function unitOptions() {
        var html = '<option value="">— الوحدة —</option>';
        $.each(config.units, function (_, u) {
            html += '<option value="' + u + '">' + word("units", u) + "</option>";
        });
        return html;
    }

    /* The same five columns as a saved condition, in the same order and the
       same widths — an editor whose new row is laid out differently from the
       rows above it reads as two different screens. */
    function row() {
        return $(
            '<div class="rc-cond-row">' +
            '  <div class="row g-2 align-items-center">' +
            '    <div class="col-md-3"><select class="form-select form-select-sm rc-left" name="leftFieldCodes" aria-label="Left field">' + options(config.left.fields) + '</select></div>' +
            '    <div class="col-md-3"><select class="form-select form-select-sm rc-cmp" name="comparisons" aria-label="Comparison">' + comparisonOptions() + '</select></div>' +
            '    <div class="col-md-3"><select class="form-select form-select-sm rc-right" name="rightFieldCodes" aria-label="Right field">' + options(config.right.fields) + '</select></div>' +
            '    <div class="col-md-1"><input class="form-control form-control-sm rc-tol" type="number" name="tolerances" placeholder="tol" aria-label="Tolerance"></div>' +
            '    <div class="col-md-2"><select class="form-select form-select-sm rc-unit" name="toleranceUnits" aria-label="Tolerance unit">' + unitOptions() + '</select></div>' +
            '  </div>' +
            '  <div class="d-flex align-items-center gap-2 mt-1 flex-wrap" dir="rtl">' +
            '    <span class="rc-help">تُقارن القيم:</span>' +
            '    <select class="form-select form-select-sm rc-norm" name="useNormalized" style="width:auto" aria-label="Compare normalized companions">' +
            '      <option value="false">كما وردت · raw</option>' +
            '      <option value="true">بعد التوحيد · normalized</option></select>' +
            '    <button class="btn btn-sm btn-link text-danger p-0 rc-remove-cond" type="button">حذف الشرط</button>' +
            '  </div>' +
            '  <div class="rc-help mt-1 rc-cond-note"></div>' +
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

        // The tolerance pair is only meaningful for two comparisons, so it
        // is only offered for those two: a box that accepts what the
        // comparison ignores is a box that invites a wrong answer.
        var needsTolerance = NEEDS_TOLERANCE[comparison] === true;
        $row.find(".rc-tol, .rc-unit").prop("disabled", !needsTolerance);
        $row.find(".rc-tol").attr("placeholder", needsTolerance ? "الفارق" : "—");

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

    /* The filter card's own builder. It writes into the same textarea the
       validator reads, so the card keeps showing exactly what the server is
       handed while nobody has to type it. The examples still write the box
       directly: the rejected one names a field the registry withholds, which
       a builder cannot draw at all — and that is the lesson of the card. */
    if (window.ReconConditions) { window.ReconConditions.attach({
        container: "#ftBuilder",
        hidden: "#ct-json",
        fields: config.left.fields
    }); }

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

    /* ---- the three rule sets ---------------------------------------
       Loading a row into its form rather than making the operator retype
       a condition tree they can see on the screen above. */
    /* The two rule sets are configured by dropdowns now, not by typing a
       condition tree. ReconConditions writes the same JSON into the same
       hidden input, so the validator and the compiler see what they always
       saw. */
    /* ---- why a builder has nothing to offer ------------------------
       Three different dead ends look identical on screen — a registry with
       no field at all, a registry whose fields are all withheld from
       matching, and a Both-sided rule whose two registries share no field
       code. Each has a different way out, so each says its own. */
    function sideOf(code) {
        return code === config.right.code ? config.right : config.left;
    }

    function datasetsLink(side) {
        return '<a href="' + config.datasetsUrl + '?id=' + side.id + '">'
            + 'افتح سجل حقول ' + side.code + '</a>';
    }

    function codesOf(fields) {
        return $.map($.grep(fields, function (f) { return f.matchable; }),
            function (f) { return f.code; });
    }

    function explainOneSide(side) {
        var matchable = codesOf(side.fields).length;

        if (side.fields.length === 0) {
            return 'سجل حقول <strong>' + side.code + '</strong> فارغ: لا حقل معرَّف فيه بعد. '
                + datasetsLink(side) + ' وأضف الحقول أولاً.';
        }

        if (matchable === 0) {
            return 'سجل حقول <strong>' + side.code + '</strong> فيه ' + side.fields.length
                + ' حقلاً، ولا حقل منها مُعلَّم <strong>قابلاً للمطابقة</strong> — ولا يظهر في'
                + ' القواعد إلا ما هو كذلك. ' + datasetsLink(side)
                + ' وعلّم الحقول التي يُسمح ببناء قواعد عليها.';
        }

        return null;
    }

    function explainClassification() {
        var chosen = $("#clSide").val();

        if (chosen !== "Both") {
            return explainOneSide(chosen === "Right" ? config.right : config.left);
        }

        var oneSided = explainOneSide(config.left) || explainOneSide(config.right);
        if (oneSided) { return oneSided; }

        // Both registries have matchable fields; they just share no code.
        return 'الطرفان لا يتشاركان أيّ كود حقل، فلا يوجد شرط يستطيع الطرفان تقييمه.'
            + ' الأيسر (<strong>' + config.left.code + '</strong>): '
            + '<span class="rc-mono">' + codesOf(config.left.fields).join(", ") + '</span>.'
            + ' الأيمن (<strong>' + config.right.code + '</strong>): '
            + '<span class="rc-mono">' + codesOf(config.right.fields).join(", ") + '</span>.'
            + ' الحلّ أن تصنعها مرّتين: '
            + '<button type="button" class="btn btn-sm btn-outline-primary py-0 px-2 rc-cb-pick"'
            + ' data-side="Left">قاعدة للطرف الأيسر</button> '
            + '<button type="button" class="btn btn-sm btn-outline-primary py-0 px-2 rc-cb-pick"'
            + ' data-side="Right">قاعدة للطرف الأيمن</button>';
    }

    var exclusionFields = function () {
        var dataset = $("#exDataset").val();
        return dataset === config.right.code ? config.right.fields : config.left.fields;
    };

    var exBuilder = window.ReconConditions && window.ReconConditions.attach({
        container: "#exBuilder",
        hidden: "#exJson",
        fields: exclusionFields(),
        nothing: function () { return explainOneSide(sideOf($("#exDataset").val())); }
    });

    if (exBuilder) {
        // An exclusion belongs to ONE dataset, so its field list follows the
        // dataset picker: offering the other side's names would offer a
        // condition the server must refuse.
        $("#exDataset").on("change", function () {
            exBuilder.fields(exclusionFields());
        });

        // The advanced box wins when it has something in it, because somebody
        // who opened it did so on purpose.
        $("#rc-exclusion-form").on("submit", function () {
            var raw = $.trim($("#exJsonRaw").val() || "");
            if (raw.length) { $("#exJson").val(raw); }
        });
    }

    $(".rc-exclusion-edit").on("click", function () {
        var $b = $(this);

        $("#exclusionRuleId").val($b.data("id"));
        $("#exDataset").val($b.data("dataset"));
        $("#exName").val($b.data("name"));
        $("#exReason").val($b.data("reason"));
        $("#exActive").prop("checked", $b.data("active") === true || $b.data("active") === "true");

        if (exBuilder) {
            exBuilder.fields(exclusionFields());
            exBuilder.load($b.data("json"));
        }

        $("#exJsonRaw").val("");
    });

    $("#rc-exclusion-reset").on("click", function () {
        $("#rc-exclusion-form")[0].reset();
        $("#exclusionRuleId").val("");
    });

    /* A classification names a side, and "Both" has to resolve against both
       registries — so its field list is the INTERSECTION by code, which is
       the only set a both-sided condition can be evaluated on. */
    function classificationFields() {
        var side = $("#clSide").val();

        if (side === "Left") { return config.left.fields; }
        if (side === "Right") { return config.right.fields; }

        var rightCodes = {};
        $.each(config.right.fields, function (_, f) { rightCodes[f.code] = true; });

        return $.grep(config.left.fields, function (f) { return rightCodes[f.code] === true; });
    }

    var clBuilder = window.ReconConditions && window.ReconConditions.attach({
        container: "#clBuilder",
        hidden: "#clJson",
        fields: classificationFields(),
        nothing: explainClassification
    });

    if (clBuilder) {
        // "Make it for the left side" has to actually move the side picker,
        // or it is advice rather than a way out.
        $("#clBuilder").on("click", ".rc-cb-pick", function () {
            $("#clSide").val($(this).data("side")).trigger("change");
        });

        $("#clSide").on("change", function () {
            clBuilder.fields(classificationFields());

            var side = $("#clSide").val();
            $("#clBuilder").siblings(".rc-cb-side-note").remove();

            if (side === "Both") {
                $("#clBuilder").after('<div class="rc-help rc-cb-side-note mt-1">'
                    + 'الحقول المعروضة هي المشتركة بين الطرفين بالكود — '
                    + 'شرط على أسماء الطرف الأيسر لا يستطيع الأيمن تقييمه.</div>');
            }
        });

        $("#rc-classification-form").on("submit", function () {
            var raw = $.trim($("#clJsonRaw").val() || "");
            if (raw.length) { $("#clJson").val(raw); }
        });
    }

    $(".rc-classification-edit").on("click", function () {
        var $b = $(this);

        $("#classificationRuleId").val($b.data("id"));
        $("#clCode").val($b.data("code"));
        $("#clName").val($b.data("label"));
        $("#clSide").val($b.data("side"));
        $("#clAction").val($b.data("action"));
        $("#clSeverity").val($b.data("severity"));
        $("#clSeq").val($b.data("seq"));
        $("#clActive").prop("checked", $b.data("active") === true || $b.data("active") === "true");

        if (clBuilder) {
            clBuilder.fields(classificationFields());
            clBuilder.load($b.data("json"));
        }

        $("#clJsonRaw").val("");
    });

    $("#rc-classification-reset").on("click", function () {
        $("#rc-classification-form")[0].reset();
        $("#classificationRuleId").val("");
    });

    /* A period-scoped check needs a window, and it must read the
       aggregates: staging is purged after a few months and a period check
       that reads it would silently narrow as the window aged out. */
    function syncScope() {
        var period = $("#ctScope").val() === "Period";

        $("#ctPeriod").prop("required", period);
        $("#ctPeriod").closest(".col-md-1").toggleClass("rc-warn-nonsargable", period && !$("#ctPeriod").val());

        if (period) {
            $("#sourceTypeA").val("RunAggregate");
            $("#sourceTypeB").val("RunAggregate");
        }
    }

    $("#ctScope, #ctPeriod").on("change keyup", syncScope);
    syncScope();
});
