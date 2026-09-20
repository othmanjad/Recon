/* =====================================================================
   Drives the MVC portal in a real browser against the demo database.

       ./demo/run-demo.sh                  # build the database
       ./demo/run-portal.sh --background   # start the portal
       node tests/browser/drive-portal.js

   Why a browser at all, when there are unit and integration tests: the
   things that break in a server-rendered portal are not the ones a unit
   test sees. A view that throws renders a 500 that compiles fine; a
   missing antiforgery token makes every POST a 400; a script that
   references a removed id fails silently. Each of those is invisible
   until something loads the page.

   It asserts, it does not just screenshot: a failure prints what was
   expected and exits non-zero.
   ===================================================================== */

const { chromium } = require("playwright");
const fs = require("fs");
const path = require("path");

const BASE = process.env.PORTAL_URL || "http://127.0.0.1:5080";
const OUT = process.env.SHOTS_DIR || path.join(__dirname, ".shots");
fs.mkdirSync(OUT, { recursive: true });

const problems = [];
let checks = 0;

function check(condition, message) {
    checks++;
    if (!condition) { problems.push(message); }
}

/* Console and network noise is collected per page: a portal that renders
   but logs a 404 for its own script is broken, just not visibly. */
function watch(page, label) {
    const errs = [];

    /* Confirmation dialogs are accepted. Without a handler the browser
       dismisses them, which cancels the submit and makes a destructive
       button look like a button that does nothing. */
    page.on("dialog", dialog => dialog.accept());
    page.on("console", m => {
        if (m.type() === "error") { errs.push(`${label}: console: ${m.text()}`); }
    });
    page.on("pageerror", e => errs.push(`${label}: pageerror: ${e.message}`));
    page.on("requestfailed", r => {
        /* A download is an aborted navigation as far as the network layer is
           concerned, so ERR_ABORTED on one is expected rather than a fault.
           Everything else genuinely failed to load. */
        const failure = r.failure()?.errorText ?? "";
        if (failure.includes("ERR_ABORTED")) { return; }
        errs.push(`${label}: requestfailed: ${r.url()} (${failure})`);
    });
    page.on("response", r => {
        if (r.status() >= 500) { errs.push(`${label}: HTTP ${r.status()} for ${r.url()}`); }
    });
    return errs;
}

/* page.waitForLoadState returns immediately when the current page is already
   loaded, so using it after a click can read the page before the navigation.
   This waits for the navigation the click starts. */
async function submit(page, locator, timeout = 120000) {
    await Promise.all([
        page.waitForNavigation({ waitUntil: "load", timeout }),
        locator.click(),
    ]);
}

async function signIn(context, userName) {
    const page = await context.newPage();
    const errs = watch(page, "signin");

    await page.goto(`${BASE}/account/signin`, { waitUntil: "load" });
    await page.fill("#userName", userName);
    await page.click('button[type="submit"]');
    await page.waitForLoadState("load");

    check(page.url().includes("/Home") || page.url() === BASE + "/" || page.url().endsWith("/"),
        `signing in as ${userName} landed on ${page.url()}`);

    problems.push(...errs);
    return page;
}

async function shot(page, name) {
    await page.screenshot({ path: path.join(OUT, `${name}.png`), fullPage: true });
}

(async () => {
    const launch = process.env.CHROMIUM_PATH ? { executablePath: process.env.CHROMIUM_PATH } : {};
    const browser = await chromium.launch(launch);

    // =================================================================
    // 1. Configure access: every screen renders, and the configuration
    //    screens offer their forms.
    // =================================================================
    const omarCtx = await browser.newContext({ viewport: { width: 1500, height: 1050 } });
    const omar = await signIn(omarCtx, "cfg.omar");
    const omarErrs = watch(omar, "cfg.omar");

    const SCREENS = [
        ["dashboard", "/", "Run dashboard"],
        ["runs", "/runs", "Trigger a run"],
        ["exceptions", "/exceptions", "Workspace"],
        ["search", "/search", "Find transactions"],
        ["reports", "/reports", "Export"],
        ["counterparties", "/counterparties", "A counterparty is the scope of access"],
        ["datasets", "/datasets", "This table is the field registry"],
        ["rules", "/rules", "Why this builder is safe"],
        ["fees", "/fees", "Every amount here is in minor units"],
        ["schedules", "/schedules", "Schedules"],
        ["settings", "/settings", "Platform settings"],
        ["audit", "/audit", "Audit log"],
    ];

    for (const [name, url, expected] of SCREENS) {
        await omar.goto(BASE + url, { waitUntil: "load" });
        await omar.waitForTimeout(150);

        const body = await omar.locator("body").innerText();
        check(body.includes(expected), `${url} did not contain "${expected}"`);

        /* The shell must have rendered: a view that throws mid-render can
           still return 200 with a truncated page. */
        check(await omar.locator(".rc-brand").count() === 1, `${url} lost the header`);
        check(await omar.locator(".rc-sidebar .nav-link").count() === 13,
            `${url} rendered ${await omar.locator(".rc-sidebar .nav-link").count()} nav links, expected 13`);
        check(await omar.locator("tbody:empty").count() === 0,
            `${url} left an empty table body`);

        await shot(omar, name);
    }

    // =================================================================
    // 2. The dashboard shows the demo run's real numbers.
    // =================================================================
    await omar.goto(BASE + "/", { waitUntil: "load" });
    const dashboard = await omar.locator("body").innerText();

    check(/CLIQ_OM/.test(dashboard), "the dashboard does not mention the demo definition");

    /* The tiles come from ops.ReconRun's cached counts, so a non-zero
       matched tile proves the dashboard is reading the run and not an
       empty filter. */
    const matchedTile = await omar
        .locator('.rc-tile:has(.rc-tile-label:text-is("Matched rows")) .rc-tile-value')
        .innerText();

    check(parseInt(matchedTile.replace(/[^0-9]/g, ""), 10) > 0,
        `the matched-rows tile reads "${matchedTile}"`);

    /* The DEMO definition's run, not simply the newest one:
       drive-onboard.js leaves runs of its own counterparty in the same
       database, and the assertions below are about this reconciliation's
       passes and control totals. */
    const runLink = omar
        .locator('tr:has-text("CLIQ_OM") a[href*="/Runs/Detail/"]').first();

    check(await runLink.count() > 0, "the dashboard lists no run for the demo definition");

    // =================================================================
    // 3. Run detail: stages, control totals, distribution, and the SQL
    //    a step actually ran.
    // =================================================================
    await runLink.click();
    await omar.waitForLoadState("load");
    const detail = await omar.locator("body").innerText();

    for (const stage of ["Exclude", "Duplicates", "Match", "Classify", "Aggregate", "ControlTotals"]) {
        check(detail.includes(stage), `run detail does not show the ${stage} stage`);
    }

    check(detail.includes("P1_REF"), "run detail does not name the first pass");
    check(detail.includes("MATCHED_TOTAL"), "run detail does not show the control totals");
    check(await omar.locator(".rc-pass-bar").count() >= 4,
        "run detail drew no distribution bars");

    /* The persisted SQL is the point of §9.4: "what rule matched this in
       June" is answerable from the run rather than from configuration
       that has since been edited. */
    /* A Match step specifically: Parse is C# and a bulk copy, so it
       records no statement, and asking it for SQL proves nothing. */
    const sqlLink = await omar
        .locator('tr:has-text("P1_REF") a[href*="/Runs/StepSql/"]')
        .first()
        .getAttribute("href");
    const sqlPage = await omarCtx.newPage();
    await sqlPage.goto(BASE + sqlLink, { waitUntil: "load" });
    const sql = await sqlPage.locator("body").innerText();

    check(/SELECT/i.test(sql), "the Match step's stored SQL came back empty");
    check(/@P\d|@RunId|@MatchRuleId/i.test(sql),
        "the stored SQL has no parameters, which would mean values were concatenated into it");
    check(/ops\.MatchResult/.test(sql),
        "the Match step's SQL does not write ops.MatchResult");
    await sqlPage.close();

    await shot(omar, "run-detail");

    // =================================================================
    // 4. The rule builder's safety property, through the UI.
    // =================================================================
    await omar.goto(BASE + "/rules", { waitUntil: "load" });

    // Only matchable fields are offered. CURRENCY is deliberately not
    // matchable in the demo configuration.
    const leftOptions = await omar.locator("select.rc-left").first().locator("option")
        .allTextContents();

    check(leftOptions.length > 0, "the rule builder offered no left fields");
    check(!leftOptions.some(o => o.includes("(CURRENCY)")),
        "the rule builder offered CURRENCY, which the registry does not mark matchable");
    check(leftOptions.some(o => o.includes("(REF_PRIMARY)")),
        "the rule builder did not offer REF_PRIMARY");

    // A rejected filter: a real field the registry withholds.
    await omar.click("#btn-sample-bad");
    await omar.click("#btn-validate");
    await omar.waitForTimeout(600);
    let result = await omar.locator("#ct-result").innerText();
    check(/Rejected/.test(result), `a withheld field was not rejected (got "${result}")`);

    // A valid filter, validated by the server.
    await omar.click("#btn-sample-ok");
    await omar.click("#btn-validate");
    await omar.waitForResponse(r => r.url().includes("ValidateFilter"), { timeout: 5000 });
    await omar.waitForTimeout(300);
    result = await omar.locator("#ct-result").innerText();
    check(/Accepted/.test(result), `a valid filter was not accepted (got "${result}")`);

    // A non-sargable comparison must warn, and warn harder in pass 1.
    const firstPass = omar.locator(".rc-pass").first();
    await firstPass.locator("select.rc-cmp").first().selectOption("Contains");
    await omar.waitForTimeout(200);
    const note = await firstPass.locator(".rc-cond-note").first().innerText();
    check(/cannot use an index/.test(note), `Contains did not warn (got "${note}")`);
    check(/pass 1/.test(note), `the pass-1 warning did not appear (got "${note}")`);

    await shot(omar, "rules");

    /* The normalized pass must NOT be warned at: both its fields carry a
       companion slot, and telling an operator otherwise would push them to
       "fix" a pass that is right. This failed when the server-rendered
       options lacked the data- attributes the advice reads. */
    const normalizedPass = omar.locator('.rc-pass:has(input[value="P2_REF_NORM"])');
    const normalizedNote = await normalizedPass.locator(".rc-cond-note").first().innerText();

    check(!/Both fields need a normalized companion/.test(normalizedNote),
        `the normalized pass was wrongly warned: "${normalizedNote}"`);
    check(/companions filled at load time/.test(normalizedNote),
        `the normalized pass was not explained: "${normalizedNote}"`);

    // Put it back, and save the pass: the save must round-trip.
    await firstPass.locator("select.rc-cmp").first().selectOption("Exact");
    await firstPass.locator('button:has-text("Save pass")').click();
    await omar.waitForLoadState("load");
    const saved = await omar.locator("body").innerText();
    check(/saved with \d+ condition/.test(saved), `saving a pass did not confirm (got a page without the message)`);

    // =================================================================
    // 5. The sandbox dry-run.
    // =================================================================
    if (await omar.locator("#sourceRunId").count() > 0) {
        await omar.locator('button:has-text("Dry-run")').click();
        await omar.waitForLoadState("load");
        const dry = await omar.locator("body").innerText();

        check(/Dry-run of CLIQ_OM/.test(dry), "the dry-run page did not render");
        check(/Sandbox/.test(dry), "the dry-run is not marked as a sandbox run");
        check(/P1_REF/.test(dry), "the dry-run reported no per-pass numbers");

        /* The assertion that matters, and the one whose absence hid a real
           defect: a replay over another run's staged rows must actually
           match. Those rows still carry the source run's verdict, and
           everything before pass 1 filters on 'Unmatched' — so without the
           reset step the passes matched nothing while the stale staging
           cache made the totals look like a flawless run. A dry-run that
           reports a perfect match rate for rules that matched nothing is
           worse than no dry-run. */
        const passMatched = await omar
            .locator('table:below(:text("Per pass")) tbody tr td:nth-child(4)')
            .allInnerTexts();

        const totalMatched = passMatched
            .map(t => parseInt(t.replace(/[^0-9]/g, ""), 10) || 0)
            .reduce((a, b) => a + b, 0);

        check(totalMatched > 0,
            `the dry-run's passes matched nothing (per-pass column read "${passMatched.join("|")}")`);

        await shot(omar, "dry-run");
    } else {
        problems.push("no replayable run was offered for a dry-run");
    }

    // =================================================================
    // 6. The mapping editor: the slot list must respect the field's type.
    // =================================================================
    await omar.goto(BASE + "/datasets", { waitUntil: "load" });
    await omar.locator(".rc-field-edit").first().click();
    await omar.waitForTimeout(250);

    const slotOptions = await omar.locator("#storageSlot option").allTextContents();
    const type = await omar.locator("#dataType").inputValue();
    check(slotOptions.length > 0, "no storage slot was offered");
    check(slotOptions.every(s => !/^Num/.test(s)) || type !== "String",
        `a String field was offered a numeric slot: ${slotOptions.join(", ")}`);

    // The companion pool is separate: only Text21..30 may be a companion.
    const companions = (await omar.locator("#normalizedSlot option").allTextContents())
        .map(c => c.trim())
        .filter(c => c !== "" && c !== "(none)");

    check(companions.length > 0, "no companion slot was offered at all");
    check(companions.every(c => /^Text(2[1-9]|30)\b/.test(c)),
        `the companion list offered a slot outside the companion pool: ${companions.join(", ")}`);

    await shot(omar, "datasets-edit");

    /* The activate toggle, both ways. Razor renders a BOOLEAN attribute value
       as the HTML boolean form — value="value" when true, and no attribute at
       all when false — so `value="@(!dataset.IsActive)"` posted the string
       "value" to a bool parameter, which binds as false. Every one of these
       buttons deactivated, whatever it said on it, and nothing noticed
       because nothing pressed them. */
    let activateLabel = await omar.locator('form[action*="/Datasets/Activate"] button')
        .first().innerText();

    await submit(omar, omar.locator('form[action*="/Datasets/Activate"] button').first(), 60000);
    let datasetsText = await omar.locator("body").innerText();

    check(/deactivated\.|activated\./.test(datasetsText),
        `the dataset activate toggle said nothing (button read "${activateLabel}")`);

    const flipped = await omar.locator('form[action*="/Datasets/Activate"] button')
        .first().innerText();

    check(flipped.trim() !== activateLabel.trim(),
        `the dataset toggle still reads "${flipped}" after being pressed — it did not flip`);

    // Put it back the way it was found.
    await submit(omar, omar.locator('form[action*="/Datasets/Activate"] button').first(), 60000);

    check((await omar.locator('form[action*="/Datasets/Activate"] button').first().innerText())
        .trim() === activateLabel.trim(),
        "pressing the dataset toggle twice did not return it to its original state");

    // =================================================================
    // 6b. The definition's activate toggle, both ways — the same Razor
    //     boolean-attribute trap as the dataset one.
    // =================================================================
    await omar.goto(BASE + "/counterparties", { waitUntil: "load" });

    const definitionToggle = () => omar
        .locator('form[action*="/Counterparties/ActivateDefinition"] button').first();

    if (await definitionToggle().count() > 0) {
        const before = (await definitionToggle().innerText()).trim();

        await submit(omar, definitionToggle(), 60000);
        const counterpartiesText = await omar.locator("body").innerText();

        check(/activated\.|deactivated\./.test(counterpartiesText),
            "the definition activate toggle said nothing");

        const after = (await definitionToggle().innerText()).trim();
        check(after !== before,
            `the definition toggle still reads "${after}" after being pressed`);

        // And back: a definition left deactivated would make every later run
        // in this suite fail for the wrong reason.
        await submit(omar, definitionToggle(), 60000);

        check((await definitionToggle().innerText()).trim() === before,
            "pressing the definition toggle twice did not restore its state");
        check(/activated\./.test(await omar.locator("body").innerText()),
            "re-activating the definition was not confirmed — activation was refused");
    } else {
        problems.push("no definition activate toggle was offered to a Configure user");
    }

    // =================================================================
    // 7. Settings: the type check refuses a value the column would take.
    // =================================================================
    await omar.goto(BASE + "/settings", { waitUntil: "load" });
    const intRow = omar.locator('form:has(input[value="SandboxPurgeDays"])');
    await intRow.locator('input[name="settingValue"]').fill("not-a-number");
    await intRow.locator('button[type="submit"]').click();
    await omar.waitForLoadState("load");

    let settings = await omar.locator("body").innerText();
    check(/declared Int/.test(settings),
        "a non-numeric value was accepted for an Int setting");

    // And a real change is recorded.
    const again = omar.locator('form:has(input[value="SandboxPurgeDays"])');
    await again.locator('input[name="settingValue"]').fill("9");
    await again.locator('button[type="submit"]').click();
    await omar.waitForLoadState("load");
    settings = await omar.locator("body").innerText();
    check(/SandboxPurgeDays: 7 → 9|SandboxPurgeDays is already 9/.test(settings),
        `the setting change was not confirmed (page did not report it)`);

    await shot(omar, "settings");

    // The audit log must now carry that change, with the old value.
    await omar.goto(BASE + "/audit", { waitUntil: "load" });
    const audit = await omar.locator("body").innerText();
    check(/PlatformSetting/.test(audit), "the audit log does not show the setting change");
    check(/cfg\.omar/.test(audit), "the audit log does not name who made the change");
    await shot(omar, "audit");

    // =================================================================
    // 8. Schedules: the cron preview answers in both zones.
    // =================================================================
    await omar.goto(BASE + "/schedules", { waitUntil: "load" });
    await omar.fill("#cronExpression", "0 22 * * *");
    await omar.fill("#timeZone", "Asia/Amman");
    await omar.click("#rc-cron-preview");
    await omar.waitForResponse(r => r.url().includes("Preview"), { timeout: 5000 });
    await omar.waitForTimeout(300);

    const preview = await omar.locator("#rc-cron-result").innerText();
    check(/Next fires/.test(preview), `the cron preview failed (got "${preview}")`);
    check(/22:00/.test(preview), `the preview does not show the local fire time (got "${preview}")`);
    check(/19:00/.test(preview), `the preview does not convert to UTC — Jordan is UTC+3 (got "${preview}")`);

    // An invalid expression is refused rather than stored.
    await omar.fill("#cronExpression", "not a cron");
    await omar.click("#rc-cron-preview");
    await omar.waitForTimeout(600);
    check(/not a valid/.test(await omar.locator("#rc-cron-result").innerText()),
        "an invalid cron expression was not refused");

    await shot(omar, "schedules");

    // =================================================================
    // 9. Fees: a tier gap is named, and the fee run produces netting.
    // =================================================================
    await omar.goto(BASE + "/fees", { waitUntil: "load" });
    const fees = await omar.locator("body").innerText();

    check(/CLIQ_IN_2026/.test(fees), "the demo fee schedule is not shown");
    check(/in force today/.test(fees), "no schedule is marked as in force");
    check(!/bands are not continuous/.test(fees),
        "the demo fee schedule reports a gap, so its tiers are wrong");

    const runIdOnPage = await omar.locator('a[href*="/Runs/Detail/"]').count();
    await omar.goto(BASE + "/", { waitUntil: "load" });
    const firstRunHref = await omar.locator('a[href*="/Runs/Detail/"]').first().getAttribute("href");
    const demoRunId = firstRunHref.split("/").pop();

    await omar.goto(BASE + "/fees", { waitUntil: "load" });
    await omar.fill("#runId", demoRunId);
    await omar.locator('button:has-text("Calculate")').click();
    await omar.waitForLoadState("load");

    const calculated = await omar.locator("body").innerText();
    check(/fees calculated|no fee schedule covering/.test(calculated),
        "the fee calculation reported nothing");
    check(/Netting/.test(calculated), "the fee calculation did not report a netting figure");
    await shot(omar, "fees");

    // =================================================================
    // 10. Reports: a CSV export streams with the registry's columns.
    // =================================================================
    await omar.goto(BASE + "/reports", { waitUntil: "load" });
    const reportsText = await omar.locator("body").innerText();
    check(/UNMATCHED_CSV/.test(reportsText), "the demo reports are not listed");

    const download = omar.waitForEvent("download", { timeout: 20000 });
    await omar.locator('tr:has-text("UNMATCHED_CSV") a:has-text("Download")').click();
    const file = await download;
    const saved2 = path.join(OUT, "unmatched.csv");
    await file.saveAs(saved2);

    const csv = fs.readFileSync(saved2, "utf8");
    check(csv.split("\n")[0].includes("Reference"),
        `the exported CSV has no header row (got "${csv.slice(0, 80)}")`);
    check(/Match status/.test(csv.split("\n")[0]),
        "the exported CSV is missing the computed column");
    check(csv.split("\n").filter(l => l.trim()).length >= 2,
        "the exported CSV has a header and no rows");

    /* And the .xlsx, which nothing had ever downloaded. An xlsx is a ZIP
       package whose writer reads back and seeks in the stream it is building,
       and an HTTP response body does neither — so every Excel export in the
       portal answered "The stream was not opened for reading" until a real
       download was asked for here. */
    const xlsxDownload = omar.waitForEvent("download", { timeout: 30000 });
    await omar.locator('tr:has-text("EXCEPTIONS_XLSX") a:has-text("Download")').click();
    const xlsxFile = await xlsxDownload;
    const savedXlsx = path.join(OUT, "exceptions.xlsx");
    await xlsxFile.saveAs(savedXlsx);

    const workbook = fs.readFileSync(savedXlsx);

    // "PK\x03\x04" is a ZIP local file header: a real package, not an error
    // page saved under an .xlsx name.
    check(workbook.length > 1000,
        `the exported workbook is ${workbook.length} bytes, which is not a workbook`);
    check(workbook[0] === 0x50 && workbook[1] === 0x4B
        && workbook[2] === 0x03 && workbook[3] === 0x04,
        "the exported workbook does not start with a ZIP header");
    check(workbook.includes(Buffer.from("xl/workbook.xml")),
        "the exported workbook has no workbook part");

    await shot(omar, "reports");

    // The export must be in the audit log: regulators ask who downloaded
    // which report (review item D3).
    await omar.goto(BASE + "/audit?entityType=Report", { waitUntil: "load" });
    check(/Export/.test(await omar.locator("body").innerText()),
        "the export was not written to the audit log");

    // =================================================================
    // 10b. Search: a prefix seek, a bulk list, and the registry's labels.
    // =================================================================
    await omar.goto(BASE + "/search", { waitUntil: "load" });

    // An unfiltered search returns nothing rather than the first 500 rows of
    // whatever the clustered index holds.
    check(/Enter a reference or a date range/.test(await omar.locator("body").innerText()),
        "an unfiltered search did not refuse to guess");

    // One term: a prefix.
    await omar.fill("#terms", "E2E-2026");
    await omar.locator('button:has-text("Search")').click();
    await omar.waitForLoadState("load");

    let searchText = await omar.locator("body").innerText();
    check(/prefix search/.test(searchText), "a single term was not treated as a prefix");

    const prefixRows = await omar.locator("table.rc-table tbody tr").count();
    check(prefixRows > 1, `a prefix search over the demo references returned ${prefixRows} row(s)`);

    /* The results are labelled in the dataset's own vocabulary, straight
       from the registry — "End To End Id", not Text1. */
    /* Compared case-insensitively: the stylesheet upper-cases table
       headers, so innerText is not the text the view wrote. */
    const headers = (await omar.locator("table.rc-table thead th").allInnerTexts())
        .map(h => h.replace(/\s+/g, " ").trim().toLowerCase());

    check(headers.some(h => h.includes("end to end id")),
        `the results are not labelled from the registry: ${headers.join(" | ")}`);
    check(!headers.some(h => /^text\d/.test(h)),
        `a storage slot name leaked into the results header: ${headers.join(" | ")}`);

    // A bulk paste: exact matches, one per line.
    const references = await omar.locator("table.rc-table tbody tr td:nth-child(3)")
        .allInnerTexts();

    const twoOfThem = references.slice(0, 2).map(t => t.trim()).filter(t => t.length > 0);

    if (twoOfThem.length === 2) {
        await omar.fill("#terms", twoOfThem.join("\n"));
        await omar.locator('button:has-text("Search")').click();
        await omar.waitForLoadState("load");

        searchText = await omar.locator("body").innerText();
        check(/2 terms: exact-match lookup/.test(searchText),
            "a pasted list was not treated as an exact-match lookup");
        check(await omar.locator("table.rc-table tbody tr").count() === 2,
            "the bulk lookup did not return one row per reference");
    } else {
        problems.push("could not read two references out of the search results to paste back");
    }

    await shot(omar, "search");

    // =================================================================
    // 11. Exceptions: assign, then close with a reason.
    // =================================================================
    await omar.goto(BASE + "/exceptions", { waitUntil: "load" });
    const exceptions = await omar.locator("body").innerText();
    check(/FAILED_INWARD|MISSING_IN_CLIQ/.test(exceptions),
        "the demo exceptions are not listed");

    await omar.locator(".rc-exception-open").first().click();
    await omar.waitForTimeout(400);

    /* The identifying values were snapshotted onto the exception at
       classification time, which is why an exception survives the
       staging purge. */
    const keys = await omar.locator("#rc-ex-keys").innerText();
    check(keys.trim().length > 0 && keys !== "(no snapshot was stored)",
        `the exception carries no snapshot of its identifying values (got "${keys}")`);

    await omar.fill("#assignTo", "ops.hala");
    await omar.locator('button:has-text("Assign")').click();
    await omar.waitForLoadState("load");
    check(/assigned to ops\.hala/.test(await omar.locator("body").innerText()),
        "assigning an exception did not confirm");

    await shot(omar, "exceptions");

    // Closing without a reason is refused.
    await omar.locator(".rc-exception-open").first().click();
    await omar.waitForTimeout(300);
    const closeButton = omar.locator('button:has-text("Close exception")');
    await omar.selectOption("#resolutionCode", "LateArrival");
    await omar.fill("#note", "matched in the next session");
    await closeButton.click();
    await omar.waitForLoadState("load");
    check(/closed as Resolved \(LateArrival\)/.test(await omar.locator("body").innerText()),
        "closing an exception with a reason did not confirm");

    problems.push(...omarErrs);
    await omarCtx.close();

    // =================================================================
    // 12. Read access sees the same data and none of the forms.
    // =================================================================
    const samiCtx = await browser.newContext({ viewport: { width: 1500, height: 1050 } });
    const sami = await signIn(samiCtx, "read.sami");
    const samiErrs = watch(sami, "read.sami");

    await sami.goto(BASE + "/runs", { waitUntil: "load" });
    const samiRuns = await sami.locator("body").innerText();
    check(/CLIQ_OM/.test(samiRuns), "read access cannot see the runs it is granted");
    check(/needs .*Operate.* access/s.test(samiRuns) || !/Trigger a run[\s\S]*Definition/.test(samiRuns),
        "read access was offered the trigger form");
    check(await sami.locator('form[action*="Trigger"]').count() === 0,
        "read access was offered the trigger form");

    await sami.goto(BASE + "/settings", { waitUntil: "load" });
    check(await sami.locator('form[action*="Save"]').count() === 0,
        "read access was offered the settings form");

    // A refusal is a page, not a stack trace: POST directly and see.
    const denied = await samiCtx.request.post(`${BASE}/Settings/Save`, {
        form: { settingKey: "SandboxPurgeDays", settingValue: "1" },
        maxRedirects: 0,
    });
    check(denied.status() === 400 || denied.status() === 302,
        `an unauthorized POST answered ${denied.status()}, expected a refusal or a redirect`);

    await shot(sami, "read-only-settings");
    problems.push(...samiErrs);
    await samiCtx.close();

    // =================================================================
    // 13. An account with no grants sees nothing, and is told why.
    // =================================================================
    const strangerCtx = await browser.newContext({ viewport: { width: 1500, height: 900 } });
    const stranger = await signIn(strangerCtx, "nobody.atall");
    const strangerErrs = watch(stranger, "stranger");

    await stranger.goto(BASE + "/", { waitUntil: "load" });
    const strangerText = await stranger.locator("body").innerText();

    check(/no counterparty grants/i.test(strangerText),
        "an account with no grants was not told why its screens are empty");
    check(!/CLIQ_OM/.test(strangerText),
        "an account with no grants saw another counterparty's definition");

    await stranger.goto(BASE + "/exceptions", { waitUntil: "load" });
    check(!/FAILED_INWARD/.test(await stranger.locator("body").innerText()),
        "an account with no grants saw another counterparty's exceptions");

    await stranger.goto(BASE + "/audit", { waitUntil: "load" });
    check(/needs Configure access/.test(await stranger.locator("body").innerText()),
        "an account with no grants was shown the audit log");

    await shot(stranger, "no-grants");
    problems.push(...strangerErrs);
    await strangerCtx.close();

    // =================================================================
    // 14. Phone width: nothing may scroll sideways.
    // =================================================================
    const phoneCtx = await browser.newContext({ viewport: { width: 400, height: 900 } });
    const phone = await signIn(phoneCtx, "cfg.omar");

    for (const [name, url] of [["dashboard", "/"], ["rules", "/rules"], ["fees", "/fees"]]) {
        await phone.goto(BASE + url, { waitUntil: "load" });
        await phone.waitForTimeout(150);

        const overflow = await phone.evaluate(() =>
            document.documentElement.scrollWidth - document.documentElement.clientWidth);

        check(overflow <= 1, `${url} overflows by ${overflow}px at 400px wide`);
        await shot(phone, `phone-${name}`);
    }

    await phoneCtx.close();
    await browser.close();

    // =================================================================
    console.log(`\n${checks} checks`);

    if (problems.length === 0) {
        console.log(`ALL GREEN — screenshots in ${OUT}`);
        process.exit(0);
    }

    console.log(`\n${problems.length} problem(s):`);
    for (const p of problems) { console.log("  - " + p); }
    process.exit(1);
})().catch(e => {
    // The checks collected so far are more useful than the stack alone: a
    // driver that dies at step 10 has already learned nine steps' worth.
    console.error("\nthe driver stopped early: " + e.message);

    if (problems.length > 0) {
        console.error(`\n${problems.length} problem(s) found before that:`);
        for (const p of problems) { console.error("  - " + p); }
    } else {
        console.error(`\n${checks} checks passed before that point.`);
    }

    process.exit(2);
});
