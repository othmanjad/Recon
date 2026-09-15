/* Report builder: load an existing report into the form, and say when a
   transaction-level scope is about to be written to a format that cannot
   hold a day of it in one sheet. */
jQuery(function ($) {
    "use strict";

    var TRANSACTION_SCOPES = { Matched: true, Unmatched: true, All: true };

    $(".rc-report-edit").on("click", function () {
        var $b = $(this);

        $("#reportDefinitionId").val($b.data("id"));
        $("#reportCode").val($b.data("code"));
        $("#reportName").val($b.data("name"));
        $("#sheetName").val($b.data("sheet"));
        $("#dataScope").val($b.data("scope"));
        $("#outputFormat").val($b.data("format"));
        $("#includeSubtotals").prop("checked", $b.data("subtotals") === true || $b.data("subtotals") === "true");
        $("#reportActive").prop("checked", $b.data("active") === true || $b.data("active") === "true");

        $("#rc-report-form-title").text("Edit " + $b.data("code"));
        warn();

        $("html, body").animate({ scrollTop: $("#rc-report-form").offset().top - 70 }, 200);
    });

    $("#rc-report-reset").on("click", function () {
        $("#rc-report-form")[0].reset();
        $("#reportDefinitionId").val("");
        $("#rc-report-form-title").text("Add a report");
        warn();
    });

    function warn() {
        var $format = $("#outputFormat");
        var transactionLevel = TRANSACTION_SCOPES[$("#dataScope").val()] === true;
        var $note = $("#rc-format-note");

        if ($note.length === 0) {
            $note = $('<div class="form-text" id="rc-format-note"></div>');
            $format.after($note);
        }

        if (transactionLevel && $format.val() === "Xlsx") {
            $note.addClass("text-warning").text(
                "A worksheet holds 1,048,576 rows. A 2M-row day is split across sheets; Csv "
                + "streams as one file.");
        } else {
            $note.removeClass("text-warning").text("");
        }
    }

    $("#dataScope, #outputFormat").on("change", warn);
    warn();
});
