/* Schedules: fill the form from a row, and ask the server what a cron
   expression would actually do.

   The preview is computed on the SERVER on purpose. The scheduler's own
   CronExpression decides when a run fires; a second implementation here
   would eventually disagree with it, and the disagreement would show up
   as a run that did not happen. */
jQuery(function ($) {
    "use strict";

    var config = window.ReconSchedules || {};

    $(".rc-schedule-edit").on("click", function () {
        var $b = $(this);

        $("#scheduleId").val($b.data("id"));
        $("#definitionId").val($b.data("definition"));
        $("#cronExpression").val($b.data("cron"));
        $("#timeZone").val($b.data("zone"));
        $("#expectedFileByTime").val($b.data("expected") || "");
        $("#isEnabled").prop("checked", $b.data("enabled") === true || $b.data("enabled") === "true");

        $("#rc-cron-result").empty();
    });

    $("#rc-cron-preview").on("click", function () {
        var $result = $("#rc-cron-result");
        $result.html('<div class="rc-help">Asking the scheduler…</div>');

        $.ajax({
            url: config.previewUrl,
            method: "POST",
            data: {
                cronExpression: $("#cronExpression").val(),
                timeZone: $("#timeZone").val()
            },
            headers: { RequestVerificationToken: config.token }
        }).done(function (response) {
            if (!response.valid) {
                $result.html('<div class="alert alert-danger mb-0 small">'
                    + escapeHtml(response.error) + "</div>");
                return;
            }

            if (!response.times || response.times.length === 0) {
                $result.html('<div class="alert alert-warning mb-0 small">'
                    + "Valid, but it does not fire within the next year.</div>");
                return;
            }

            var html = '<div class="alert alert-success mb-0 small"><strong>Next fires</strong> in '
                + escapeHtml(response.zone) + ':<ul class="mb-0 mt-1">';

            $.each(response.times, function (_, t) {
                html += "<li><span class=\"rc-mono\">" + escapeHtml(t.local)
                    + "</span> local — <span class=\"rc-mono\">" + escapeHtml(t.utc)
                    + "</span> UTC</li>";
            });

            $result.html(html + "</ul></div>");
        }).fail(function () {
            $result.html('<div class="alert alert-danger mb-0 small">The preview request failed.</div>');
        });
    });

    function escapeHtml(text) {
        return $("<div>").text(text === null || text === undefined ? "" : text).html();
    }
});
