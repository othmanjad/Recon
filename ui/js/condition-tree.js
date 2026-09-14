/* =====================================================================
   The condition tree — ONE schema, used by every filter in the platform.

   Design review B5: LeftFilter / RightFilter / ConditionJson / FilterJson
   were all described as "structured JSON" with no schema defined, and
   cfg.SqlSourceDefinition.FilterExpression was free SQL text — an
   injection surface in the exact place the design promised none (D1).

   The schema:

     {
       "op": "and" | "or",
       "items": [
         { "field": "<FieldCode>",
           "cmp": "eq|ne|gt|gte|lt|lte|in|isnull|isnotnull",
           "value": <scalar | array for "in"> },
         { "op": "or", "items": [ ... ] }          // nesting allowed
       ]
     }

   Used by: match-rule filters, classification rules, exclusion rules,
   SQL source filters, and report filters. Validated on save here AND on
   the server; the client copy exists to give Operations an immediate
   error, never as the security boundary.

   THE RULE THAT MATTERS: "field" is resolved against the dataset's field
   registry. A field code that is not in the registry, or is in it but not
   matchable, is rejected — it never reaches SQL. Values are parameterized
   by the compiler; nothing here concatenates a value into a string.
   ===================================================================== */

window.ReconConditionTree = (function ($) {
    "use strict";

    var OPS = ["and", "or"];

    var COMPARATORS = {
        eq:        { label: "equals",            arity: 1 },
        ne:        { label: "does not equal",    arity: 1 },
        gt:        { label: "greater than",      arity: 1 },
        gte:       { label: "greater or equal",  arity: 1 },
        lt:        { label: "less than",         arity: 1 },
        lte:       { label: "less or equal",     arity: 1 },
        "in":      { label: "is one of",         arity: "many" },
        isnull:    { label: "is empty",          arity: 0 },
        isnotnull: { label: "is not empty",      arity: 0 }
    };

    /* A condition tree deeper than this is a sign the user is building
       something the rule engine should not be asked to compile. */
    var MAX_DEPTH = 8;

    /* ------------------------------------------------------------------
       validate(tree, dataset) -> { valid: bool, errors: [string] }

       `dataset` supplies the field registry. Pass null to validate shape
       only (used by unit tests); real saves must pass the dataset, or an
       unknown field code would sail through.
       ------------------------------------------------------------------ */
    function validate(tree, dataset) {
        var errors = [];
        var codes = {};

        if (dataset && dataset.fields) {
            $.each(dataset.fields, function (_, f) { codes[f.code] = f; });
        }

        function walk(node, path, depth) {
            if (depth > MAX_DEPTH) {
                errors.push(path + ": nested deeper than " + MAX_DEPTH + " levels");
                return;
            }
            if (node === null || typeof node !== "object" || $.isArray(node)) {
                errors.push(path + ": expected an object");
                return;
            }

            var isGroup = Object.prototype.hasOwnProperty.call(node, "op");
            var isLeaf = Object.prototype.hasOwnProperty.call(node, "field");

            if (isGroup && isLeaf) {
                errors.push(path + ": a node is either a group (op/items) or a condition (field/cmp), not both");
                return;
            }

            if (isGroup) {
                if ($.inArray(node.op, OPS) === -1) {
                    errors.push(path + ".op: must be " + OPS.join(" or ") + ', got "' + node.op + '"');
                }
                if (!$.isArray(node.items) || node.items.length === 0) {
                    errors.push(path + ".items: must be a non-empty array");
                    return;
                }
                $.each(node.items, function (i, child) {
                    walk(child, path + ".items[" + i + "]", depth + 1);
                });
                return;
            }

            if (!isLeaf) {
                errors.push(path + ": neither a group (op/items) nor a condition (field/cmp)");
                return;
            }

            /* ---- the security-relevant check ---- */
            if (typeof node.field !== "string" || node.field === "") {
                errors.push(path + ".field: must be a non-empty field code");
            } else if (dataset) {
                var f = codes[node.field];
                if (!f) {
                    errors.push(path + '.field: "' + node.field
                              + '" is not in the field registry of dataset ' + dataset.code);
                } else if (!f.matchable) {
                    errors.push(path + '.field: "' + node.field
                              + '" is not marked matchable and cannot appear in a condition');
                }
            }

            var cmp = COMPARATORS[node.cmp];
            if (!cmp) {
                errors.push(path + '.cmp: unknown comparator "' + node.cmp + '"');
                return;
            }

            var hasValue = Object.prototype.hasOwnProperty.call(node, "value");
            if (cmp.arity === 0 && hasValue) {
                errors.push(path + ".value: " + node.cmp + " takes no value");
            } else if (cmp.arity === 1) {
                if (!hasValue || node.value === null || node.value === "") {
                    errors.push(path + ".value: " + node.cmp + " requires a value");
                } else if ($.isArray(node.value)) {
                    errors.push(path + ".value: " + node.cmp + " takes a single value, not a list");
                }
            } else if (cmp.arity === "many") {
                if (!$.isArray(node.value) || node.value.length === 0) {
                    errors.push(path + ".value: in requires a non-empty list");
                }
            }
        }

        walk(tree, "$", 1);
        return { valid: errors.length === 0, errors: errors };
    }

    function parseAndValidate(json, dataset) {
        var tree;
        try {
            tree = JSON.parse(json);
        } catch (e) {
            return { valid: false, errors: ["not valid JSON: " + e.message], tree: null };
        }
        var r = validate(tree, dataset);
        r.tree = tree;
        return r;
    }

    /* ------------------------------------------------------------------
       describe(tree, dataset) -> readable English, for the rule summary
       line Operations reads before activating anything.
       ------------------------------------------------------------------ */
    function describe(tree, dataset) {
        var labels = {};
        if (dataset && dataset.fields) {
            $.each(dataset.fields, function (_, f) { labels[f.code] = f.label; });
        }

        function render(node) {
            if (!node || typeof node !== "object") { return "?"; }
            if (Object.prototype.hasOwnProperty.call(node, "op")) {
                var parts = $.map(node.items || [], function (c) { return render(c); });
                if (parts.length === 0) { return "?"; }
                if (parts.length === 1) { return parts[0]; }
                return "(" + parts.join(node.op === "or" ? " OR " : " AND ") + ")";
            }
            var name = labels[node.field] || node.field;
            var cmp = COMPARATORS[node.cmp];
            var verb = cmp ? cmp.label : node.cmp;
            if (!cmp || cmp.arity === 0) { return name + " " + verb; }
            if (cmp.arity === "many") {
                return name + " " + verb + " [" + ($.isArray(node.value) ? node.value.join(", ") : "") + "]";
            }
            return name + " " + verb + ' "' + node.value + '"';
        }

        return render(tree);
    }

    return {
        OPS: OPS,
        COMPARATORS: COMPARATORS,
        MAX_DEPTH: MAX_DEPTH,
        validate: validate,
        parseAndValidate: parseAndValidate,
        describe: describe
    };
}(jQuery));
