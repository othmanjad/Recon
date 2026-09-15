/* Fee screens: fill the schedule form from the schedule chosen for
   editing, and the tier form from the tier chosen for editing. Nothing
   here computes a fee — the arithmetic is integer minor units on the
   server, and a JavaScript preview using doubles would be the one place
   in the system where a fee is a float. */
jQuery(function ($) {
    "use strict";

    $("select[data-rc-schedules]").on("change", function () {
        var $o = $(this).find("option:selected");

        if (!$o.val()) {
            $("#scheduleCode, #scheduleName, #transactionType, #effectiveTo").val("");
            return;
        }

        $("#scheduleCode").val($o.data("code"));
        $("#scheduleName").val($o.data("name"));
        $("#direction").val($o.data("direction"));
        $("#transactionType").val($o.data("txntype") || "");
        $("#feeParty").val($o.data("party"));
        $("#currencyCode").val($o.data("currency"));
        $("#roundingMode").val($o.data("rounding"));
        $("#effectiveFrom").val($o.data("from"));
        $("#effectiveTo").val($o.data("to") || "");
        $("#scheduleActive").prop("checked", $o.data("active") === true || $o.data("active") === "true");
    });

    $(".rc-tier-edit").on("click", function () {
        var $b = $(this);
        var $form = $('.rc-tier-form[data-schedule="' + $b.data("schedule") + '"]');

        if ($form.length === 0) { return; }

        $form.find(".rc-tier-id").val($b.data("id"));
        $form.find(".rc-tier-from").val($b.data("from"));
        $form.find(".rc-tier-to").val($b.data("to") === undefined ? "" : $b.data("to"));
        $form.find(".rc-tier-calc").val($b.data("calc"));
        $form.find(".rc-tier-fixed").val($b.data("fixed"));
        $form.find(".rc-tier-pct").val($b.data("pct"));
        $form.find(".rc-tier-min").val($b.data("min") === undefined ? "" : $b.data("min"));
        $form.find(".rc-tier-max").val($b.data("max") === undefined ? "" : $b.data("max"));
    });
});
