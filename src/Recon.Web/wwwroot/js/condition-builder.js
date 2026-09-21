/* =====================================================================
   A visual builder for the condition tree.

   The exclusion and classification editors used to ask an operator to type
   this by hand:

       {"op":"and","items":[{"field":"STATUS","cmp":"eq","value":"RJCT"}]}

   Which is a code, in a product whose central claim is that Operations
   configures it without writing any. This builds the same JSON from
   dropdowns and writes it into the hidden input the form already posts, so
   the server, the validator and the compiler see exactly what they saw
   before — nothing downstream changes.

   Flat AND/OR on purpose. Nested groups are in the schema and the compiler
   handles them, but every real exclusion and classification rule in the
   design is a list of conditions joined one way; offering nesting here
   would cost the clarity that is the whole point. The JSON box is still
   there, one click away, for a rule that needs more.
   ===================================================================== */
window.ReconConditions = (function ($) {
    "use strict";

    /* The operator vocabulary of the condition tree, in words. The keys are
       the schema's own tokens — see Recon.Domain.Conditions.ConditionNode. */
    var OPERATORS = [
        { cmp: "eq", label: "يساوي", en: "equals", values: 1 },
        { cmp: "ne", label: "لا يساوي", en: "does not equal", values: 1 },
        { cmp: "in", label: "واحد من", en: "is one of", values: "list" },
        { cmp: "gt", label: "أكبر من", en: "greater than", values: 1 },
        { cmp: "gte", label: "أكبر من أو يساوي", en: "at least", values: 1 },
        { cmp: "lt", label: "أصغر من", en: "less than", values: 1 },
        { cmp: "lte", label: "أصغر من أو يساوي", en: "at most", values: 1 },
        { cmp: "isnull", label: "فارغ", en: "is empty", values: 0 },
        { cmp: "isnotnull", label: "غير فارغ", en: "is not empty", values: 0 }
    ];

    function operatorOf(cmp) {
        for (var i = 0; i < OPERATORS.length; i++) {
            if (OPERATORS[i].cmp === cmp) { return OPERATORS[i]; }
        }
        return OPERATORS[0];
    }

    function escape(text) {
        return $("<div>").text(text == null ? "" : text).html();
    }

    /* A field is offered only if the registry marks it matchable: that flag
       is the security boundary, and a builder that offered more than the
       server accepts would be a form that teaches people to be refused. */
    function fieldOptions(fields, selected) {
        var html = "";
        $.each(fields, function (_, f) {
            if (f.matchable === false) { return; }
            html += '<option value="' + escape(f.code) + '"'
                + (f.code === selected ? " selected" : "") + ">"
                + escape(f.label) + " (" + escape(f.code) + ")</option>";
        });
        return html;
    }

    function operatorOptions(selected) {
        var html = "";
        $.each(OPERATORS, function (_, o) {
            html += '<option value="' + o.cmp + '"'
                + (o.cmp === selected ? " selected" : "") + ">"
                + escape(o.label) + " · " + escape(o.en) + "</option>";
        });
        return html;
    }

    function attach(options) {
        var $host = $(options.container);
        var $hidden = $(options.hidden);
        if (!$host.length || !$hidden.length) { return null; }

        var api = {};
        var fields = options.fields || [];

        $host.addClass("rc-cb").html(
            '<div class="rc-cb-head d-flex align-items-center gap-2 flex-wrap mb-2">'
            + '  <span class="rc-help">يتحقق الشرط عندما</span>'
            + '  <select class="form-select form-select-sm rc-cb-op" style="width:auto">'
            + '    <option value="and">كل الشروط التالية · ALL</option>'
            + '    <option value="or">أيّ شرط منها · ANY</option>'
            + '  </select>'
            + '  <span class="rc-help">تتحقق</span>'
            + '</div>'
            + '<div class="rc-cb-rows d-flex flex-column gap-2"></div>'
            + '<div class="d-flex align-items-center gap-2 mt-2 flex-wrap">'
            + '  <button class="btn btn-sm btn-outline-primary rc-cb-add" type="button">'
            + '    <i class="bi bi-plus-lg" aria-hidden="true"></i> أضف شرطاً</button>'
            + '  <span class="rc-cb-preview rc-help rc-mono"></span>'
            + '</div>');

        var $rows = $host.find(".rc-cb-rows");

        function row(leaf) {
            leaf = leaf || {};
            var $row = $(
                '<div class="rc-cb-row row g-2 align-items-center">'
                + '  <div class="col-md-5"><select class="form-select form-select-sm rc-cb-field"'
                + '      aria-label="الحقل">' + fieldOptions(fields, leaf.field) + '</select></div>'
                + '  <div class="col-md-3"><select class="form-select form-select-sm rc-cb-cmp"'
                + '      aria-label="المقارنة">' + operatorOptions(leaf.cmp) + '</select></div>'
                + '  <div class="col-md-3"><input class="form-control form-control-sm rc-cb-value"'
                + '      aria-label="القيمة"></div>'
                + '  <div class="col-md-1 text-end"><button class="btn btn-sm btn-link text-danger p-0'
                + '      rc-cb-remove" type="button" aria-label="حذف الشرط">حذف</button></div>'
                + '</div>');

            if (leaf.value !== undefined && leaf.value !== null) {
                $row.find(".rc-cb-value").val(
                    $.isArray(leaf.value) ? leaf.value.join(", ") : String(leaf.value));
            }

            return $row;
        }

        /* The value box follows the operator: a list for "one of", nothing at
           all for "is empty". A box that accepts what the operator cannot use
           is a box that invites a refusal. */
        function syncRow($row) {
            var op = operatorOf($row.find(".rc-cb-cmp").val());
            var $value = $row.find(".rc-cb-value");

            if (op.values === 0) {
                $value.val("").prop("disabled", true).attr("placeholder", "— لا قيمة —");
            } else if (op.values === "list") {
                $value.prop("disabled", false)
                    .attr("placeholder", "قيم مفصولة بفاصلة: RJCT, RJCF");
            } else {
                $value.prop("disabled", false).attr("placeholder", "القيمة");
            }
        }

        function tree() {
            var items = [];

            $rows.find(".rc-cb-row").each(function () {
                var $row = $(this);
                var field = $row.find(".rc-cb-field").val();
                if (!field) { return; }

                var op = operatorOf($row.find(".rc-cb-cmp").val());
                var leaf = { field: field, cmp: op.cmp };
                var raw = $.trim($row.find(".rc-cb-value").val() || "");

                if (op.values === "list") {
                    leaf.value = $.map(raw.split(","), function (v) {
                        v = $.trim(v);
                        return v.length ? v : null;
                    });
                } else if (op.values === 1) {
                    leaf.value = raw;
                }

                items.push(leaf);
            });

            return { op: $host.find(".rc-cb-op").val() || "and", items: items };
        }

        function sentence(built) {
            if (!built.items.length) { return "لم تُضف شروط بعد"; }

            var joiner = built.op === "or" ? " أو " : " و ";

            return $.map(built.items, function (leaf) {
                var op = operatorOf(leaf.cmp);
                var value = op.values === 0 ? ""
                    : " " + ($.isArray(leaf.value) ? leaf.value.join(" / ") : leaf.value);
                return leaf.field + " " + op.label + value;
            }).join(joiner);
        }

        function publish() {
            var built = tree();

            // An empty builder writes an empty string rather than a tree with
            // no items: the server requires a condition, and "{}" would pass
            // that check while meaning nothing.
            $hidden.val(built.items.length ? JSON.stringify(built) : "");
            $host.find(".rc-cb-preview").text(sentence(built));
        }

        api.publish = publish;

        api.load = function (json) {
            $rows.empty();

            var parsed = null;
            try { parsed = json ? JSON.parse(json) : null; } catch (e) { parsed = null; }

            if (parsed && parsed.items && parsed.items.length) {
                $host.find(".rc-cb-op").val(parsed.op === "or" ? "or" : "and");

                $.each(parsed.items, function (_, leaf) {
                    // A nested group cannot be drawn by a flat builder. Rather
                    // than lose it, the JSON box keeps it and the builder says
                    // so instead of pretending.
                    if (leaf && leaf.field) {
                        var $r = row(leaf);
                        $rows.append($r);
                        syncRow($r);
                    }
                });
            }

            if (!$rows.find(".rc-cb-row").length) {
                var $first = row();
                $rows.append($first);
                syncRow($first);
            }

            publish();
        };

        api.fields = function (next) {
            fields = next || [];

            // Keep what still exists on the new side, drop what does not.
            $rows.find(".rc-cb-row").each(function () {
                var $select = $(this).find(".rc-cb-field");
                var current = $select.val();
                $select.html(fieldOptions(fields, current));

                if (!$select.val()) { $select.prop("selectedIndex", 0); }
            });

            publish();
        };

        $host.on("click", ".rc-cb-add", function () {
            var $r = row();
            $rows.append($r);
            syncRow($r);
            publish();
        });

        $host.on("click", ".rc-cb-remove", function () {
            $(this).closest(".rc-cb-row").remove();

            if (!$rows.find(".rc-cb-row").length) {
                var $r = row();
                $rows.append($r);
                syncRow($r);
            }

            publish();
        });

        $host.on("change", ".rc-cb-cmp", function () {
            syncRow($(this).closest(".rc-cb-row"));
            publish();
        });

        // "input" as well as "keyup": a value pasted, autofilled or set by a
        // script raises input and nothing else, and a condition whose value
        // never reached the hidden field is a rule that silently matches the
        // empty string.
        $host.on("change input keyup", ".rc-cb-field, .rc-cb-value, .rc-cb-op", publish);

        // And once more as the form is submitted, so a value typed and sent
        // with the Enter key cannot outrun the keyup handler.
        $hidden.closest("form").on("submit", publish);

        api.load($hidden.val());
        return api;
    }

    return { attach: attach, operators: OPERATORS };
}(jQuery));
