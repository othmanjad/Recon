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
        $.each(offered(fields), function (_, f) {
            html += '<option value="' + escape(f.code) + '"'
                + (f.code === selected ? " selected" : "") + ">"
                + escape(f.label) + " (" + escape(f.code) + ")</option>";
        });
        return html;
    }

    function offered(fields) {
        return $.grep(fields || [], function (f) { return f.matchable !== false; });
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

        /* What to say when there is no field to offer. The caller supplies it
           because only the caller knows which dead end this is — and a dead
           end the screen cannot name is one the person cannot get out of. */
        function reason() {
            var text = options.nothing ? options.nothing() : null;

            return text || 'لا حقول متاحة لبناء شرط هنا: الحقل يظهر في هذه القائمة فقط'
                + ' إذا كان مُعرَّفاً في سجل حقول المجموعة و<strong>قابلاً للمطابقة</strong>.';
        }

        /* The head, the rows and the add button live in one plain wrapper so
           that hiding them is one call — and so that hiding them WORKS:
           jQuery's .toggle() writes an inline display, which Bootstrap's
           d-flex (display:flex !important) silently overrides. */
        $host.addClass("rc-cb").html(
            '<div class="rc-cb-controls">'
            + '<div class="rc-cb-head d-flex align-items-center gap-2 flex-wrap mb-2" dir="rtl">'
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
            + '</div>'
            + '</div>'
            // The sentence gets a line of its own, labelled. It is the one
            // thing on the form that says what is about to be saved, and as a
            // grey aside beside a button nobody read it.
            + '<div class="alert alert-light border mt-2 mb-0 py-2 px-3 small" dir="rtl">'
            + '  <strong>ما سيُحفَظ:</strong> <span class="rc-cb-preview"></span>'
            + '  <span class="rc-cb-count rc-help d-block mt-1"></span>'
            + '</div>');

        var $rows = $host.find(".rc-cb-rows");

        function row(leaf) {
            leaf = leaf || {};
            var $row = $(
                '<div class="rc-cb-row row g-2 align-items-center">'
                + '  <div class="col-md-4"><select class="form-select form-select-sm rc-cb-field"'
                + '      aria-label="الحقل">' + fieldOptions(fields, leaf.field) + '</select></div>'
                + '  <div class="col-md-4"><select class="form-select form-select-sm rc-cb-cmp"'
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
            // Not "nothing yet": nothing is a refusal on save, and the line
            // that reads the condition out is where that belongs.
            if (!built.items.length) {
                return "لا شرط بعد — والحفظ سيُرفَض: قاعدة بلا شرط تنطبق على كل صفّ.";
            }

            var joiner = built.op === "or" ? " أو " : " و ";

            return $.map(built.items, function (leaf) {
                var op = operatorOf(leaf.cmp);
                var value = op.values === 0 ? ""
                    : " " + ($.isArray(leaf.value) ? leaf.value.join(" / ") : leaf.value);
                return leaf.field + " " + op.label + value;
            }).join(joiner);
        }

        /* The advanced box, when the caller has one. Somebody who opened it
           and typed in it did so on purpose, so it wins — and it wins HERE,
           in the one place that decides what gets posted, rather than in a
           second submit handler that has to out-order this one. */
        function advanced() {
            return options.advanced ? $.trim($(options.advanced).val() || "") : "";
        }

        function publish() {
            var built = tree();
            var raw = advanced();

            // An empty builder writes an empty string rather than a tree with
            // no items: the server requires a condition, and "{}" would pass
            // that check while meaning nothing.
            $hidden.val(raw || (built.items.length ? JSON.stringify(built) : ""));
            $host.find(".rc-cb-preview").text(
                raw ? "الشرط المكتوب في صندوق JSON المتقدّم (يستبدل ما بُني أعلاه)"
                    : sentence(built));
            $host.find(".rc-cb-count").text(
                "الحقول المتاحة لهذا الشرط: " + offered(fields).length);
        }

        api.publish = publish;

        /* No field to offer is a state, not an empty list. It happens when a
           dataset has nothing marked matchable yet, and when the side is Both
           and the two registries share no field code — and a builder that
           draws an empty dropdown for it sends a blank condition the server
           then has to refuse. So it says so, and hides the controls that
           cannot work. */
        function sayIfNothingToOffer() {
            var empty = offered(fields).length === 0;

            $host.find(".rc-cb-empty").remove();
            $host.find(".rc-cb-controls").toggle(!empty);

            if (empty) {
                $rows.empty();
                $host.prepend('<div class="alert alert-warning py-2 px-3 mb-2 small rc-cb-empty"'
                    + ' dir="rtl" role="alert">' + reason() + '</div>');
            }

            return empty;
        }

        api.load = function (json) {
            $rows.empty();

            if (sayIfNothingToOffer()) { publish(); return; }

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

            if (sayIfNothingToOffer()) { publish(); return; }

            // Keep what still exists on the new side, drop what does not.
            $rows.find(".rc-cb-row").each(function () {
                var $select = $(this).find(".rc-cb-field");
                var current = $select.val();
                $select.html(fieldOptions(fields, current));

                if (!$select.val()) { $select.prop("selectedIndex", 0); }
            });

            // Coming back from the empty state there is no row to update: it
            // was removed when there was nothing to put in it, and a builder
            // that returns to a side with fields and still shows none is a
            // dead end the person was told they had escaped.
            if (!$rows.find(".rc-cb-row").length) {
                var $first = row();
                $rows.append($first);
                syncRow($first);
            }

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

        if (options.advanced) { $(options.advanced).on("change input keyup", publish); }

        // And once more as the form is submitted, so a value typed and sent
        // with the Enter key cannot outrun the keyup handler. A submit with
        // no condition at all is stopped here: the server refuses it anyway,
        // and the refusal costs the person everything else they had typed.
        $hidden.closest("form").on("submit", function (e) {
            publish();

            if ($hidden.val()) { return; }

            e.preventDefault();

            // Give them the row the message is about to point at: if the
            // fields are there, the only thing missing is somewhere to pick
            // them, and a message naming a row that is not on screen is a
            // message that cannot be followed.
            if (offered(fields).length && !$rows.find(".rc-cb-row").length) {
                var $fresh = row();
                $rows.append($fresh);
                syncRow($fresh);
            }

            $host.find(".rc-cb-blocked").remove();
            $host.append('<div class="alert alert-danger py-2 px-3 mt-2 mb-0 small rc-cb-blocked"'
                + ' dir="rtl" role="alert">لم يُحفَظ: القاعدة تحتاج شرطاً واحداً على الأقل.'
                + ' ' + (offered(fields).length === 0
                    ? 'ولا حقل متاح هنا — اقرأ السبب أعلاه.'
                    : 'اختر حقلاً ومقارنة من الصف أعلاه.') + '</div>');

            $host[0].scrollIntoView({ block: "center" });
        });

        // The refusal clears itself as soon as the builder has something.
        $host.on("change input", function () {
            if ($hidden.val()) { $host.find(".rc-cb-blocked").remove(); }
        });

        api.load($hidden.val());
        return api;
    }

    return { attach: attach, operators: OPERATORS };
}(jQuery));
