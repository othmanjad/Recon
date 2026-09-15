/* Fills the definition form from the definition chosen for editing. The
   dataset selects are matched by code, because the form carries codes for
   display and ids for submission. */
jQuery(function ($) {
    "use strict";

    $("select[data-rc-definitions]").on("change", function () {
        var $option = $(this).find("option:selected");

        if (!$option.val()) {
            $("#defCode, #defName").val("");
            $("#windowBefore, #windowAfter").val(1);
            return;
        }

        $("#defCode").val($option.data("code"));
        $("#defName").val($option.data("name"));
        $("#windowBefore").val($option.data("before"));
        $("#windowAfter").val($option.data("after"));

        selectByCode("#leftDatasetId", $option.data("left"));
        selectByCode("#rightDatasetId", $option.data("right"));
    });

    function selectByCode(selector, code) {
        $(selector).find("option").each(function () {
            if ($(this).data("code") === code) {
                $(selector).val($(this).val());
            }
        });
    }
});
