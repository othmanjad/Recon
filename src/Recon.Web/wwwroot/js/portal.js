/* =====================================================================
   Portal-wide behaviour. Two things only:

     1. A confirmation for the destructive buttons that carry
        data-rc-confirm. The server does not rely on it — a revoke or a
        delete is authorized server-side regardless — it exists so that
        a mis-click is not the same as an intention.

     2. Marking negative numbers. The formatter emits the digits; the
        colour is presentation and belongs here.
   ===================================================================== */

jQuery(function ($) {
    "use strict";

    $(document).on("click", "[data-rc-confirm]", function (event) {
        if (!window.confirm($(this).data("rc-confirm"))) {
            event.preventDefault();
            event.stopImmediatePropagation();
        }
    });

    $(".rc-num").each(function () {
        var $cell = $(this);
        if (/^\s*-/.test($cell.text())) {
            $cell.addClass("rc-num-neg");
        }
    });
});
