(function ($) {
    "use strict";
    var M = window.ReconMock, R = window.Recon;

    $(function () {
        R.renderShell({ title: "Platform settings" });

        var html = "";
        $.each(M.settings, function (_, s) {
            var open = s.desc.indexOf("OPEN") === 0;
            html += "<tr>"
                 + "<td><code class='rc-field'>" + R.escapeHtml(s.key) + "</code></td>"
                 + '<td><input class="form-control form-control-sm rc-num" value="'
                 + R.escapeHtml(s.value) + '"></td>'
                 + '<td class="rc-help">' + R.escapeHtml(s.type) + "</td>"
                 + '<td class="small' + (open ? " text-warning-emphasis" : "") + '">'
                 + (open ? '<i class="bi bi-question-circle"></i> ' : "")
                 + R.escapeHtml(s.desc) + "</td>"
                 + "</tr>";
        });
        $("#set-rows").html(html);
    });
}(jQuery));
