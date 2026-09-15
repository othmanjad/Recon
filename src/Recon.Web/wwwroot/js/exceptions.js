/* =====================================================================
   The exception workspace's one piece of client script: filling the
   modal from the row the operator clicked.

   Everything that changes data is an ordinary form POST with an
   antiforgery token — there is no fetch-and-patch path, so an operator
   with JavaScript disabled loses the modal and nothing else.
   ===================================================================== */

jQuery(function ($) {
    "use strict";

    $(".rc-exception-open").on("click", function () {
        var $button = $(this);
        var id = $button.data("id");

        $("#rc-ex-id").text(id);
        $(".rc-ex-input-id").val(id);
        $("#assignTo").val($button.data("assigned") || "");

        /* The snapshotted identifying values. Pretty-printed when it is
           JSON, shown verbatim when it is not: an exception whose
           snapshot is malformed should show what is actually stored
           rather than an empty box. */
        var keys = $button.data("keys");
        var text = "(no snapshot was stored)";

        if (keys) {
            try {
                text = JSON.stringify(JSON.parse(keys), null, 2);
            } catch (e) {
                text = String(keys);
            }
        }

        $("#rc-ex-keys").text(text);
    });
});
