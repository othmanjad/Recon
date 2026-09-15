/* =====================================================================
   The mapping editor's client side.

   Two jobs, both of which exist to stop a save the database would
   refuse anyway — the point is that the operator learns before the
   round trip, not that the check lives here:

     1. The storage-slot list is filtered to slots whose type matches
        the field's declared type. A trigger refuses a mismatch
        (THROW 50003), and a dropdown offering Num3 for a String field
        is an invitation to hit it.

     2. The companion slot is only enabled when the field is marked for
        normalization AND is a String. A companion on a Decimal field is
        refused by CK_DatasetField_Norm, and a companion set while
        NormalizeForMatch is 0 is refused by CK_DatasetField_NormSlotSet.
   ===================================================================== */

jQuery(function ($) {
    "use strict";

    var $type = $("#dataType");
    var $slot = $("#storageSlot");
    var $normalize = $("#normalizeForMatch");
    var $normSlot = $("#normalizedSlot");
    var $hint = $("#rc-field-hint");

    /* The full slot list, captured before any filtering removes options. */
    var allSlots = $slot.find("option").map(function () {
        var $o = $(this);
        return {
            value: $o.val(),
            text: $o.text(),
            type: $o.data("type"),
            used: $o.data("used") === true || $o.data("used") === "true"
        };
    }).get();

    function filterSlots(keepValue) {
        var wanted = $type.val();
        var current = keepValue || $slot.val();

        $slot.empty();

        $.each(allSlots, function (_, slot) {
            if (slot.type !== wanted) { return; }

            $slot.append(
                $("<option>").val(slot.value).text(slot.text)
                    /* A slot already taken by another field of this dataset is
                       shown and marked rather than hidden: it is exactly what
                       the operator needs to pick when EDITING that field. */
                    .prop("selected", slot.value === current)
            );
        });

        if ($slot.find("option").length === 0) {
            $slot.append($("<option>").val("").text("no slot of this type is defined"));
        }
    }

    function syncNormalization() {
        var isString = $type.val() === "String";
        var on = $normalize.is(":checked");

        $normalize.prop("disabled", !isString);
        $normSlot.prop("disabled", !(isString && on));

        if (!isString) {
            $normalize.prop("checked", false);
            $normSlot.val("");
            $hint.text("A normalized companion only makes sense for a String field.");
        } else if (on && !$normSlot.val()) {
            $hint.text("Choose a companion slot: normalization without one is refused.");
        } else if (on) {
            $hint.text("Rules can compare the companion with an indexed Exact match.");
        } else {
            $normSlot.val("");
            $hint.text("");
        }
    }

    $type.on("change", function () { filterSlots(); syncNormalization(); });
    $normalize.on("change", syncNormalization);
    $normSlot.on("change", syncNormalization);

    /* ---- editing an existing field ---------------------------------- */
    $(".rc-field-edit").on("click", function () {
        var $b = $(this);

        $("#datasetFieldId").val($b.data("id"));
        $("#fieldCode").val($b.data("code"));
        $("#displayLabel").val($b.data("label"));
        $("#dataType").val($b.data("type"));
        $("#fieldRole").val($b.data("role") || "");
        $("#displayOrder").val($b.data("order"));

        $("#isMatchable").prop("checked", $b.data("matchable") === true || $b.data("matchable") === "true");
        $("#isIndexed").prop("checked", $b.data("indexed") === true || $b.data("indexed") === "true");
        $("#isRequired").prop("checked", $b.data("required") === true || $b.data("required") === "true");
        $("#normalizeForMatch").prop("checked", $b.data("normalize") === true || $b.data("normalize") === "true");

        filterSlots($b.data("slot"));
        $("#normalizedSlot").val($b.data("normslot") || "");
        syncNormalization();

        $("#rc-field-editor-title").text("Edit " + $b.data("code"));
        $("html, body").animate({ scrollTop: $("#rc-field-editor").offset().top - 70 }, 200);
    });

    $("#rc-field-reset").on("click", function () {
        $("#rc-field-form")[0].reset();
        $("#datasetFieldId").val("");
        $("#rc-field-editor-title").text("Add a field");
        filterSlots();
        syncNormalization();
    });

    filterSlots();
    syncNormalization();
});
