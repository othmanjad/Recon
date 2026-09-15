/* =====================================================================
   Setup: hide the SQL login fields under Windows authentication, and
   test a connection before it is written to disk.

   The test is a server call, not a guess: only the server can say
   whether the login works, whether the database exists, and which
   edition is answering.
   ===================================================================== */

jQuery(function ($) {
    "use strict";

    var config = window.ReconSetup || {};

    function syncAuth() {
        var windowsAuth = $("#integratedSecurity").is(":checked");
        $(".rc-sql-auth").toggle(!windowsAuth);

        if (windowsAuth) {
            $("#userId, #password").val("");
        }
    }

    $("#integratedSecurity").on("change", syncAuth);
    syncAuth();

    $("#rc-test").on("click", function () {
        var $result = $("#rc-test-result");
        $result.html('<div class="rc-help">Asking the server…</div>');

        $.ajax({
            url: config.testUrl,
            method: "POST",
            data: $("#rc-connection-form").serialize(),
            headers: { RequestVerificationToken: config.token }
        }).done(function (response) {
            if (!response.ok) {
                $result.html('<div class="alert alert-danger mb-0 small"><strong>No.</strong> '
                    + escapeHtml(response.message) + "</div>");
                return;
            }

            var tone = response.databaseExists ? "success" : "warning";

            $result.html('<div class="alert alert-' + tone + ' mb-0 small"><strong>'
                + (response.databaseExists ? "Connected." : "Server reachable.")
                + "</strong> " + escapeHtml(response.message)
                + '<div class="rc-mono mt-1">' + escapeHtml(response.version || "")
                + (response.edition ? " · " + escapeHtml(response.edition) : "")
                + "</div></div>");
        }).fail(function (xhr) {
            $result.html('<div class="alert alert-danger mb-0 small">The test request failed'
                + (xhr.status ? " (HTTP " + xhr.status + ")" : "") + ".</div>");
        });
    });

    function escapeHtml(text) {
        return $("<div>").text(text === null || text === undefined ? "" : text).html();
    }
});
