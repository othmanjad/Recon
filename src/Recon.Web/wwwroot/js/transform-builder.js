/* =====================================================================
   A visual builder for a field mapping's transform chain.

   The mapping editor used to ask for this by hand:

       [{"op":"Trim"},{"op":"Upper"}]

   — the same "type the JSON" problem the condition boxes had, on the one
   screen an operator visits BEFORE the rules: a transform chain is how a
   partner's vocabulary becomes the platform's, and getting it wrong is
   how two sides that mean the same thing never match.

   Rows, in order, each one an operation with its own arguments, plus a
   preview that runs the chain on a value the person types. The preview is
   a courtesy — the server parses and validates the chain on save, with
   the engine's own parser, and that is the boundary.
   ===================================================================== */
window.ReconTransforms = (function ($) {
    "use strict";

    /* The operations, in the order they are offered. "args" says what the
       row draws; the keys are the schema's own — see
       Recon.Engine.Parsing.Transforms.Operations, which the server also
       renders into the help text, so the two cannot drift apart. */
    var OPERATIONS = [
        { op: "Map", label: "ترجمة القيم", en: "Map values", args: "map",
          hint: "قيمة كاملة تصير قيمة أخرى — مثل true ← Inward" },
        { op: "Trim", label: "احذف الفراغات حول القيمة", en: "Trim", args: "none" },
        { op: "Upper", label: "حروف كبيرة", en: "Upper", args: "none" },
        { op: "Lower", label: "حروف صغيرة", en: "Lower", args: "none" },
        { op: "Normalize", label: "توحيد: حروف كبيرة بلا رموز ولا فراغات", en: "Normalize", args: "none" },
        { op: "StripWhitespace", label: "احذف كل الفراغات", en: "StripWhitespace", args: "none" },
        { op: "StripNonAlphanumeric", label: "أبقِ الحروف والأرقام فقط", en: "StripNonAlphanumeric", args: "none" },
        { op: "StripLeadingZeros", label: "احذف الأصفار البادئة", en: "StripLeadingZeros", args: "none" },
        { op: "Substring", label: "اقتطع جزءاً بالموضع", en: "Substring", args: "substring" },
        { op: "RegexExtract", label: "استخرج بنمط (regex)", en: "RegexExtract", args: "regex",
          hint: "⚠ يعمل على كل قيمة في الملف" },
        { op: "Replace", label: "استبدل نصاً داخل القيمة", en: "Replace", args: "replace",
          hint: "استبدال جزئي: يطال النصّ أينما ورد داخل القيمة" }
    ];

    function operationOf(op) {
        for (var i = 0; i < OPERATIONS.length; i++) {
            if (OPERATIONS[i].op === op) { return OPERATIONS[i]; }
        }
        return null;
    }

    function escape(text) {
        return $("<div>").text(text == null ? "" : text).html();
    }

    /* ---- the same operations, in the browser, for the preview ---------
       Kept faithful to Transforms.ApplyOne on the server. Where it cannot
       be (a .NET regex is not a JavaScript one), the preview says so
       rather than showing a result the load would not produce. */
    function applyOne(value, step) {
        switch (step.op) {
            case "Trim": return value.replace(/^\s+|\s+$/g, "");
            case "Upper": return value.toUpperCase();
            case "Lower": return value.toLowerCase();
            case "StripWhitespace": return value.replace(/\s/g, "");
            case "StripNonAlphanumeric": return value.replace(/[^0-9A-Za-z؀-ۿ]/g, "");
            case "StripLeadingZeros":
                return value.replace(/^0+/, "").length ? value.replace(/^0+/, "") : "0";
            case "Normalize":
                return value.replace(/[^0-9A-Za-z؀-ۿ]/g, "").toUpperCase();
            case "Substring":
                var start = parseInt(step.start, 10) || 0;
                return step.length === null || step.length === undefined || step.length === ""
                    ? value.substring(start)
                    : value.substr(start, parseInt(step.length, 10));
            case "RegexExtract":
                try {
                    var m = new RegExp(step.pattern).exec(value);
                    if (!m) { return ""; }
                    return m[parseInt(step.group, 10) || 0] || "";
                } catch (e) { return value; }
            case "Replace":
                return value.split(step.from).join(step.to || "");
            case "Map":
                var cases = step.cases || [];
                for (var i = 0; i < cases.length; i++) {
                    var same = step.ignoreCase === false
                        ? value === cases[i].from
                        : value.toUpperCase() === String(cases[i].from).toUpperCase();

                    if (same) { return cases[i].to || ""; }
                }
                return step.otherwise === null || step.otherwise === undefined
                    ? value : step.otherwise;
            default: return value;
        }
    }

    function attach(options) {
        var $host = $(options.container);
        var $hidden = $(options.hidden);
        if (!$host.length || !$hidden.length) { return null; }

        var api = {};

        var operationOptions = (function () {
            var html = "";
            $.each(OPERATIONS, function (_, o) {
                html += '<option value="' + o.op + '">'
                    + escape(o.label) + " · " + escape(o.en) + "</option>";
            });
            return html;
        }());

        $host.addClass("rc-tx").html(
            '<div class="rc-tx-rows d-flex flex-column gap-2"></div>'
            + '<div class="d-flex align-items-center gap-2 mt-2 flex-wrap">'
            + '  <button class="btn btn-sm btn-outline-primary rc-tx-add" type="button">'
            + '    <i class="bi bi-plus-lg" aria-hidden="true"></i> أضف خطوة</button>'
            + '  <span class="rc-help">الخطوات تُطبَّق بالترتيب من الأعلى إلى الأسفل.</span>'
            + '</div>'
            + '<div class="alert alert-light border mt-2 mb-0 py-2 px-3 small" dir="rtl">'
            + '  <div class="d-flex align-items-center gap-2 flex-wrap">'
            + '    <label class="mb-0" for="rc-tx-sample">جرِّب على قيمة:</label>'
            + '    <input class="form-control form-control-sm rc-mono rc-tx-sample" id="rc-tx-sample"'
            + '      style="width:220px" placeholder="true">'
            + '  </div>'
            + '  <div class="rc-tx-preview mt-1"></div>'
            + '</div>');

        var $rows = $host.find(".rc-tx-rows");

        function mapPair(one) {
            one = one || {};
            return '<div class="rc-tx-pair d-flex align-items-center gap-2 mb-1 flex-wrap" dir="rtl">'
                + '<span class="rc-help">إذا كانت القيمة</span>'
                + '<input class="form-control form-control-sm rc-mono rc-tx-from" style="width:150px"'
                + '  value="' + escape(one.from) + '" placeholder="true" aria-label="القيمة الواردة">'
                + '<span class="rc-help">اجعلها</span>'
                + '<input class="form-control form-control-sm rc-mono rc-tx-to" style="width:150px"'
                + '  value="' + escape(one.to) + '" placeholder="Inward" aria-label="القيمة المخزَّنة">'
                + '<button class="btn btn-sm btn-link text-danger p-0 rc-tx-pair-remove" type="button">حذف</button>'
                + '</div>';
        }

        function argsHtml(step) {
            var spec = operationOf(step.op) || OPERATIONS[0];

            if (spec.args === "substring") {
                return '<div class="d-flex align-items-center gap-2 flex-wrap" dir="rtl">'
                    + '<span class="rc-help">من الموضع</span>'
                    + '<input type="number" min="0" class="form-control form-control-sm rc-tx-start"'
                    + ' style="width:100px" value="' + escape(step.start) + '" aria-label="البداية">'
                    + '<span class="rc-help">بطول</span>'
                    + '<input type="number" min="0" class="form-control form-control-sm rc-tx-length"'
                    + ' style="width:100px" value="' + escape(step.length) + '"'
                    + ' placeholder="حتى النهاية" aria-label="الطول"></div>';
            }

            if (spec.args === "regex") {
                return '<div class="d-flex align-items-center gap-2 flex-wrap" dir="rtl">'
                    + '<span class="rc-help">النمط</span>'
                    + '<input class="form-control form-control-sm rc-mono rc-tx-pattern" dir="ltr"'
                    + ' style="width:280px" value="' + escape(step.pattern) + '" aria-label="النمط">'
                    + '<span class="rc-help">المجموعة</span>'
                    + '<input type="number" min="0" class="form-control form-control-sm rc-tx-group"'
                    + ' style="width:90px" value="' + escape(step.group) + '" aria-label="رقم المجموعة"></div>';
            }

            if (spec.args === "replace") {
                return '<div class="d-flex align-items-center gap-2 flex-wrap" dir="rtl">'
                    + '<span class="rc-help">استبدل</span>'
                    + '<input class="form-control form-control-sm rc-mono rc-tx-from" style="width:160px"'
                    + ' value="' + escape(step.from) + '" aria-label="النصّ">'
                    + '<span class="rc-help">بـ</span>'
                    + '<input class="form-control form-control-sm rc-mono rc-tx-to" style="width:160px"'
                    + ' value="' + escape(step.to) + '" aria-label="البديل"></div>';
            }

            if (spec.args === "map") {
                var pairs = (step.cases && step.cases.length ? step.cases : [{}, {}]);
                var icId = "rc-tx-ic-" + Math.random().toString(36).slice(2);
                var html = '<div class="rc-tx-pairs">';
                $.each(pairs, function (_, one) { html += mapPair(one); });

                return html + '</div>'
                    + '<div class="d-flex align-items-center gap-3 flex-wrap mt-1" dir="rtl">'
                    + '<button class="btn btn-sm btn-outline-secondary py-0 px-2 rc-tx-pair-add"'
                    + ' type="button">+ قيمة أخرى</button>'
                    + '<span class="rc-help">وأيّ قيمة غير هذه:</span>'
                    + '<select class="form-select form-select-sm rc-tx-else-mode" style="width:auto">'
                    + '  <option value="keep">تُترك كما وردت</option>'
                    + '  <option value="set">تصير قيمة ثابتة</option>'
                    + '</select>'
                    + '<input class="form-control form-control-sm rc-mono rc-tx-else" style="width:150px"'
                    + ' value="' + escape(step.otherwise) + '" placeholder="UNKNOWN"'
                    + ' aria-label="قيمة البدائل">'
                    + '<div class="form-check mb-0">'
                    + '<input class="form-check-input rc-tx-ignorecase" type="checkbox" id="' + icId + '"'
                    + (step.ignoreCase === false ? "" : " checked") + '>'
                    + '<label class="form-check-label rc-help" for="' + icId + '">'
                    + 'تجاهل حالة الأحرف</label></div>'
                    + '</div>';
            }

            return '<span class="rc-help">' + escape(spec.hint || "لا إعدادات لهذه الخطوة.") + '</span>';
        }

        function row(step) {
            step = step || { op: "Map" };

            var $row = $('<div class="rc-tx-row border rounded p-2">'
                + '<div class="d-flex align-items-center gap-2 flex-wrap mb-1" dir="rtl">'
                + '  <select class="form-select form-select-sm rc-tx-op" style="width:auto"'
                + '    aria-label="الخطوة">' + operationOptions + '</select>'
                + '  <div class="ms-auto d-flex gap-2">'
                + '    <button class="btn btn-sm btn-link p-0 rc-tx-up" type="button" '
                + '      aria-label="حرّك لأعلى">▲</button>'
                + '    <button class="btn btn-sm btn-link p-0 rc-tx-down" type="button" '
                + '      aria-label="حرّك لأسفل">▼</button>'
                + '    <button class="btn btn-sm btn-link text-danger p-0 rc-tx-remove" type="button">'
                + '      حذف الخطوة</button>'
                + '  </div>'
                + '</div>'
                + '<div class="rc-tx-args"></div>'
                + '</div>');

            $row.find(".rc-tx-op").val(step.op);
            $row.find(".rc-tx-args").html(argsHtml(step));
            $row.find(".rc-tx-else-mode").val(
                step.otherwise === null || step.otherwise === undefined ? "keep" : "set");

            return $row;
        }

        /* The "otherwise" box is only meaningful when the chain says what a
           stranger becomes, so it follows its own picker. */
        function syncElse($row) {
            var set = $row.find(".rc-tx-else-mode").val() === "set";
            $row.find(".rc-tx-else").prop("disabled", !set).toggleClass("d-none", !set);
        }

        function stepOf($row) {
            var op = $row.find(".rc-tx-op").val();
            var step = { op: op };

            if (op === "Substring") {
                step.start = parseInt($row.find(".rc-tx-start").val(), 10) || 0;
                var length = $.trim($row.find(".rc-tx-length").val() || "");
                if (length.length) { step.length = parseInt(length, 10); }
            } else if (op === "RegexExtract") {
                step.pattern = $row.find(".rc-tx-pattern").val() || "";
                var group = $.trim($row.find(".rc-tx-group").val() || "");
                if (group.length) { step.group = parseInt(group, 10); }
            } else if (op === "Replace") {
                step.from = $row.find(".rc-tx-from").val() || "";
                step.to = $row.find(".rc-tx-to").val() || "";
            } else if (op === "Map") {
                step.cases = [];

                $row.find(".rc-tx-pair").each(function () {
                    var from = $(this).find(".rc-tx-from").val();
                    if (!from || !from.length) { return; }
                    step.cases.push({ from: from, to: $(this).find(".rc-tx-to").val() || "" });
                });

                if ($row.find(".rc-tx-else-mode").val() === "set") {
                    step.otherwise = $row.find(".rc-tx-else").val() || "";
                }

                if (!$row.find(".rc-tx-ignorecase").is(":checked")) { step.ignoreCase = false; }
            }

            return step;
        }

        function chain() {
            var steps = [];

            $rows.find(".rc-tx-row").each(function () {
                var step = stepOf($(this));

                // A Map with no pair yet is a step that does nothing; leaving
                // it out keeps the saved chain to what was actually asked for,
                // and the server refuses an empty Map anyway.
                if (step.op === "Map" && (!step.cases || step.cases.length === 0)) { return; }

                steps.push(step);
            });

            return steps;
        }

        function preview(steps) {
            var sample = $host.find(".rc-tx-sample").val();

            if (!steps.length) {
                return "لا خطوات: القيمة تُخزَّن كما وردت في الملف.";
            }

            if (sample === undefined || sample === "") {
                return "اكتب قيمة أعلاه لترى أثر الخطوات عليها.";
            }

            var value = sample;
            var trail = [sample];

            $.each(steps, function (_, step) {
                value = applyOne(String(value), step);
                trail.push(value);
            });

            return trail.join("  ←  ") + (
                $.grep(steps, function (s) { return s.op === "RegexExtract"; }).length
                    ? "   (معاينة تقريبية: أنماط الـ regex تُنفَّذ على الخادم)" : "");
        }

        function advanced() {
            return options.advanced ? $.trim($(options.advanced).val() || "") : "";
        }

        function publish() {
            var steps = chain();
            var raw = advanced();

            // An empty chain writes an empty string, not "[]": the column is
            // nullable and "no transforms" is the absence of a chain. And the
            // advanced box wins when somebody has opened it and typed in it.
            $hidden.val(raw || (steps.length ? JSON.stringify(steps) : ""));
            $host.find(".rc-tx-preview").text(preview(steps));
        }

        api.publish = publish;

        api.load = function (json) {
            $rows.empty();

            var parsed = null;
            try { parsed = json ? JSON.parse(json) : null; } catch (e) { parsed = null; }

            if ($.isArray(parsed)) {
                $.each(parsed, function (_, step) {
                    if (step && step.op) {
                        var $r = row(step);
                        $rows.append($r);
                        syncElse($r);
                    }
                });
            }

            publish();
        };

        $host.on("click", ".rc-tx-add", function () {
            var $r = row();
            $rows.append($r);
            syncElse($r);
            publish();
        });

        $host.on("click", ".rc-tx-remove", function () {
            $(this).closest(".rc-tx-row").remove();
            publish();
        });

        $host.on("click", ".rc-tx-pair-add", function () {
            $(this).closest(".rc-tx-row").find(".rc-tx-pairs").append(mapPair());
            publish();
        });

        $host.on("click", ".rc-tx-pair-remove", function () {
            var $row = $(this).closest(".rc-tx-row");
            $(this).closest(".rc-tx-pair").remove();

            if (!$row.find(".rc-tx-pair").length) { $row.find(".rc-tx-pairs").append(mapPair()); }
            publish();
        });

        // Order is the chain's meaning: trimming after stripping symbols is a
        // different chain from stripping after trimming.
        $host.on("click", ".rc-tx-up", function () {
            var $row = $(this).closest(".rc-tx-row");
            $row.prev(".rc-tx-row").before($row);
            publish();
        });

        $host.on("click", ".rc-tx-down", function () {
            var $row = $(this).closest(".rc-tx-row");
            $row.next(".rc-tx-row").after($row);
            publish();
        });

        $host.on("change", ".rc-tx-op", function () {
            var $row = $(this).closest(".rc-tx-row");
            $row.find(".rc-tx-args").html(argsHtml({ op: $(this).val() }));
            syncElse($row);
            publish();
        });

        $host.on("change", ".rc-tx-else-mode", function () {
            syncElse($(this).closest(".rc-tx-row"));
            publish();
        });

        $host.on("change input keyup", "input, select", publish);

        if (options.advanced) { $(options.advanced).on("change input keyup", publish); }

        $hidden.closest("form").on("submit", publish);

        api.load($hidden.val());
        return api;
    }

    return { attach: attach, operations: OPERATIONS };
}(jQuery));
