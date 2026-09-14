(function ($) {
    "use strict";
    var M = window.ReconMock, R = window.Recon;

    var STEPS = [
        "Create the counterparty.",
        "Define a dataset: choose the provider, upload a sample file, and the system proposes a field registry from it.",
        "Review and adjust the field mappings, labels and roles.",
        "Define the opposite dataset — often an OM view or stored procedure.",
        "Build the rule set: pick field pairs and comparison types, pass by pass.",
        "<strong>Dry-run against the sample</strong> and read the match rate per pass. Not optional: without it Operations activates broken rules against production data, and the platform loses its credibility the first time.",
        "Define classifications, control totals and reports.",
        "Activate — the scheduler picks it up. No deployment, no developer."
    ];

    $(function () {
        R.renderShell({ title: "Counterparties" });

        var html = "";
        $.each(M.counterparties, function (_, c) {
            html += "<tr>"
                 + "<td><code class='rc-field'>" + R.escapeHtml(c.code) + "</code></td>"
                 + "<td>" + R.escapeHtml(c.name) + "</td>"
                 + '<td class="rc-num">' + c.datasets + "</td>"
                 + '<td class="rc-num">' + c.definitions + "</td>"
                 + "<td>" + (c.active
                     ? '<span class="badge text-bg-success">active</span>'
                     : '<span class="badge text-bg-secondary">draft</span>') + "</td>"
                 + '<td class="text-end"><a class="btn btn-sm btn-outline-secondary" href="datasets.html">Datasets</a></td>'
                 + "</tr>";
        });
        $("#cp-rows").html(html);

        var steps = "";
        $.each(STEPS, function (_, s) { steps += "<li class='mb-2'>" + s + "</li>"; });
        $("#onboard-steps").html(steps);
    });
}(jQuery));
