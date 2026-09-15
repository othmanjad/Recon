/* =====================================================================
   The upload form's two conveniences:

     1. Name which dataset each file belongs to, from the definition
        chosen. "Left file" means nothing until it says CLIQ_SESSION.

     2. Say that a large file takes time. A 2M-row session is minutes of
        parsing and staging, and a button that looks idle invites a
        second click — which the run lock would refuse, correctly, and
        confusingly.
   ===================================================================== */

jQuery(function ($) {
    "use strict";

    var $definition = $("select[data-rc-definition-sides]");

    function sides() {
        var $option = $definition.find("option:selected");
        $("[data-rc-left]").text($option.data("left") || "—");
        $("[data-rc-right]").text($option.data("right") || "—");
    }

    $definition.on("change", sides);
    sides();

    $definition.closest("form").on("submit", function () {
        var $button = $("#rc-upload-submit");

        /* Disabled after submit, not before: a disabled button in a form
           that failed validation would leave the operator stuck. */
        window.setTimeout(function () {
            $button.prop("disabled", true).text("Running…");
        }, 0);
    });
});
