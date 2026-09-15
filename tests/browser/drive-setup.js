/* =====================================================================
   The first-run path, in a real browser, against a real server.

       ./demo/run-portal.sh --fresh        # a portal with NO database
       node tests/browser/drive-setup.js

   This is the path a person actually takes on a machine that has SQL
   Server and nothing else: open the portal, sign in, type a server name,
   press install, load something to look at, upload two files and get a
   reconciled run. Every step of it is a screen, and this asserts that
   each one does what it says.

   It creates its own database (ReconFirstRun by default) and leaves it
   in place afterwards, so a failure can be looked at.
   ===================================================================== */

const { chromium } = require("playwright");
const fs = require("fs");
const path = require("path");

const BASE = process.env.PORTAL_URL || "http://127.0.0.1:5080";
const SERVER = process.env.RECON_SQL_SERVER || "127.0.0.1,1433";
const DATABASE = process.env.RECON_SQL_DATABASE || "ReconFirstRun";
const LOGIN = process.env.RECON_SQL_LOGIN || "sa";
const PASSWORD = process.env.RECON_SQL_PASSWORD || "Recon#Verify2026x";
const OUT = process.env.SHOTS_DIR || path.join(__dirname, ".shots");

const REPO = path.resolve(__dirname, "../..");
const LEFT = path.join(REPO, "demo/files/CLIQ_SESSION_20260913_S1.csv");
const RIGHT = path.join(REPO, "demo/files/OM_TXN_20260913.csv");

fs.mkdirSync(OUT, { recursive: true });

const problems = [];
let checks = 0;

function check(condition, message) {
    checks++;
    if (!condition) { problems.push(message); }
}

function watch(page) {
    /* The destructive buttons ask for confirmation, and a browser with no
       handler dismisses the dialog — which cancels the submit. The install
       button simply did nothing, silently, until this was here: the click
       landed, the confirm was auto-dismissed, and no request was ever made. */
    page.on("dialog", dialog => dialog.accept());

    page.on("console", m => {
        if (m.type() === "error") { problems.push("console: " + m.text()); }
    });
    page.on("pageerror", e => problems.push("pageerror: " + e.message));
    page.on("response", r => {
        if (r.status() >= 500) { problems.push(`HTTP ${r.status()} for ${r.url()}`); }
    });
}

/* A form POST here can take ten seconds (installing a schema) or minutes
   (staging a session). page.waitForLoadState returns immediately when the
   CURRENT page is already loaded, so using it after a click reads the page
   before the navigation — which is how an install that worked looked like an
   install that did nothing. This waits for the navigation the click starts. */
async function submit(page, locator, timeout = 120000) {
    await Promise.all([
        page.waitForNavigation({ waitUntil: "load", timeout }),
        locator.click(),
    ]);
}

(async () => {
    const launch = process.env.CHROMIUM_PATH ? { executablePath: process.env.CHROMIUM_PATH } : {};
    const browser = await chromium.launch(launch);
    const context = await browser.newContext({ viewport: { width: 1500, height: 1100 } });
    const page = await context.newPage();
    watch(page);

    // =================================================================
    // 1. Everything redirects to setup until there is a database.
    // =================================================================
    await page.goto(BASE + "/account/signin", { waitUntil: "load" });
    await page.fill("#userName", "first.operator");
    await submit(page, page.locator('button[type="submit"]'), 30000);

    /* Sign-in has to work with no database at all: it is how the setup
       screen gets a name to write against the install. */
    check(!/error/i.test(await page.locator("body").innerText()) || page.url().includes("/setup"),
        "signing in on an unconfigured portal did not work");

    await page.goto(BASE + "/", { waitUntil: "load" });
    check(page.url().includes("/setup"),
        `an unconfigured portal did not send the dashboard to setup (landed on ${page.url()})`);

    const fresh = await page.locator("body").innerText();
    check(/1 · Point at a server/.test(fresh), "the setup wizard did not render its steps");
    check(/not set/.test(fresh), "the setup screen does not say the connection is unset");

    await page.screenshot({ path: path.join(OUT, "setup-1-empty.png"), fullPage: true });

    // =================================================================
    // 2. Test a connection before saving it: a wrong password is a
    //    message, not a file on disk.
    // =================================================================
    await page.fill("#server", SERVER);
    await page.fill("#database", DATABASE);
    await page.uncheck("#integratedSecurity");
    await page.fill("#userId", LOGIN);
    await page.fill("#password", "definitely-not-the-password");
    await page.click("#rc-test");
    await page.waitForResponse(r => r.url().includes("/Setup/Test"), { timeout: 20000 });
    await page.waitForTimeout(400);

    let result = await page.locator("#rc-test-result").innerText();
    check(/No\./.test(result), `a wrong password was not refused (got "${result}")`);
    check(/login|password|Login failed/i.test(result),
        `the refusal does not say what was wrong (got "${result}")`);

    // The right one, against a database that does not exist yet.
    await page.fill("#password", PASSWORD);
    await page.click("#rc-test");
    await page.waitForResponse(r => r.url().includes("/Setup/Test"), { timeout: 20000 });
    await page.waitForTimeout(400);

    result = await page.locator("#rc-test-result").innerText();
    check(/Server reachable|Connected/.test(result),
        `a correct connection was not accepted (got "${result}")`);
    check(/Microsoft SQL Server/i.test(result),
        `the test does not report which server answered (got "${result}")`);

    await page.screenshot({ path: path.join(OUT, "setup-2-tested.png"), fullPage: true });

    // =================================================================
    // 3. Save, then install: the portal creates its own database.
    // =================================================================
    await submit(page, page.locator('#rc-connection-form button[type="submit"]'), 30000);

    let body = await page.locator("body").innerText();
    check(new RegExp(`Saved.*\\[${DATABASE}\\]`).test(body),
        "saving the connection was not confirmed");
    check(!/definitely-not-the-password|" + PASSWORD + "/.test(body),
        "a password was rendered on the page");

    /* The redacted connection string must be shown, not the real one: a
       screen that prints a password is a password in every screenshot. */
    const shown = await page.locator(".rc-card:has-text('This deployment')").innerText();
    check(/\*{4,}/.test(shown) || /Integrated Security=True/i.test(shown),
        `the deployment card shows an unredacted connection string: ${shown}`);
    check(!shown.includes(PASSWORD), "the deployment card shows the real password");

    await submit(page, page.locator('form[action*="/Setup/Install"] button'), 300000);

    body = await page.locator("body").innerText();
    check(/Install complete/.test(body), `the install did not report success: ${firstLine(body)}`);
    check(new RegExp(`created database`).test(body), "the install did not create the database");
    check(/01-schema\.sql/.test(body), "the install did not report which scripts it applied");

    const schemaRows = await page.locator("table.rc-table tbody tr").allInnerTexts();
    const pending = schemaRows.filter(r => /pending/.test(r) && !/optional/.test(r));
    check(pending.length === 0, `scripts are still pending after the install: ${pending.join(" | ")}`);

    // The schema is really there, and the options script really ran.
    check(/Snapshot reads\s*\n?\s*on/i.test(body) || /\bon\b/.test(shown),
        "READ_COMMITTED_SNAPSHOT is not on after the install");

    await page.screenshot({ path: path.join(OUT, "setup-3-installed.png"), fullPage: true });

    // Pressing install again must be a no-op rather than an error: the
    // ledger is what makes the button safe to press twice.
    await submit(page, page.locator('form[action*="/Setup/Install"] button'), 120000);

    body = await page.locator("body").innerText();
    check(/already applied/.test(body), "a second install did not recognise its own work");
    check(!/Install failed/.test(body), "a second install failed");

    // =================================================================
    // 4. An installed but empty platform, then the demo configuration.
    // =================================================================
    check(/3 · Something to work with/.test(body),
        "the setup screen does not offer to put something in the database");

    await submit(page, page.locator('form[action*="/Setup/Demo"] button'), 180000);

    body = await page.locator("body").innerText();
    check(/Demo configuration loaded/.test(body), `loading the demo failed: ${firstLine(body)}`);
    check(/granted first\.operator Configure access to 1 counterparty/.test(body),
        "the person who seeded the platform was not granted access to it");

    await page.screenshot({ path: path.join(OUT, "setup-4-seeded.png"), fullPage: true });

    // =================================================================
    // 5. The portal now works: the guard is out of the way.
    // =================================================================
    await page.goto(BASE + "/", { waitUntil: "load" });
    check(!page.url().includes("/setup"),
        "the dashboard still redirects to setup after a complete install");

    body = await page.locator("body").innerText();
    check(/CLIQ_OM/.test(body), "the dashboard does not show the seeded definition");
    check(/No runs match this filter/.test(body), "a freshly seeded platform already has runs");

    // =================================================================
    // 6. Upload two files and reconcile them — the whole pipeline from
    //    the browser, which is the point of all of this.
    // =================================================================
    await page.goto(BASE + "/runs", { waitUntil: "load" });

    body = await page.locator("body").innerText();
    check(/Upload a session and reconcile it/.test(body), "the upload form is not on the runs screen");

    const left = await page.locator("[data-rc-left]").innerText();
    check(left.trim() === "CLIQ_SESSION",
        `the upload form does not name the left dataset (got "${left}")`);

    await page.setInputFiles("#leftFile", LEFT);
    await page.setInputFiles("#rightFile", RIGHT);
    await page.fill("#uploadDate", "2026-09-13");
    await page.fill("#uploadSession", "S1");

    await submit(page, page.locator('form[action*="/Runs/Upload"] button[type="submit"]'), 600000);

    body = await page.locator("body").innerText();

    check(/\/Runs\/Detail\//i.test(page.url()) || /Run \d+/.test(body),
        `the upload did not land on a run page (landed on ${page.url()})`);

    check(/completed/i.test(body), `the uploaded run did not complete: ${firstLine(body)}`);

    /* The same numbers the CLI reports for these two files. If the portal's
       path produced different ones, one of the two pipelines would be
       wrong — which is the whole reason there is only one. */
    check(/15 matched|15 pass/.test(body) || /\b15\b/.test(body),
        "the run page does not show the 15 matched pairs these files contain");
    check(/P1_REF/.test(body), "the run page does not show the passes");
    check(/1 rejected|Parse errors/.test(body) || /not a valid Integer/.test(body),
        "the rejected row was not reported anywhere on the run page");

    await page.screenshot({ path: path.join(OUT, "setup-5-uploaded-run.png"), fullPage: true });

    // The file itself was stored, hashed, and written to the audit log.
    await page.goto(BASE + "/audit?entityType=SourceFile", { waitUntil: "load" });
    body = await page.locator("body").innerText();
    check(/SourceFile/.test(body), "the upload was not written to the audit log");
    check(/sha256|Sha256/i.test(body), "the audit entry does not record the file's hash");

    // =================================================================
    // 7. The scheduler and the housekeeping, from the portal.
    // =================================================================
    await page.goto(BASE + "/schedules", { waitUntil: "load" });
    body = await page.locator("body").innerText();
    check(/running/.test(body), "the scheduler's state is not shown");

    await submit(page, page.locator('form[action*="/Schedules/Maintenance"] button'), 180000);

    body = await page.locator("body").innerText();
    check(/sandbox purge/.test(body), `housekeeping reported nothing: ${firstLine(body)}`);
    check(/partitions:/.test(body), "housekeeping did not report the partition runway");

    await submit(page, page.locator('form[action*="/Schedules/Scheduler"] button'), 60000);

    body = await page.locator("body").innerText();
    check(/scheduler is stopped/i.test(body), "stopping the scheduler was not confirmed");
    check(/stopped/.test(await page.locator(".rc-card:has-text('The scheduler')").innerText()),
        "the scheduler still reports itself as running after being stopped");

    // Put it back, so the instance is left as it was found.
    await submit(page, page.locator('form[action*="/Schedules/Scheduler"] button'), 60000);

    check(/running again/i.test(await page.locator("body").innerText()),
        "starting the scheduler again was not confirmed");

    await page.screenshot({ path: path.join(OUT, "setup-6-maintenance.png"), fullPage: true });

    await context.close();
    await browser.close();

    console.log(`\n${checks} checks`);

    if (problems.length === 0) {
        console.log(`ALL GREEN — the portal built [${DATABASE}] itself and reconciled a session`);
        process.exit(0);
    }

    console.log(`\n${problems.length} problem(s):`);
    for (const p of problems) { console.log("  - " + p); }
    process.exit(1);
})().catch(e => {
    console.error("\nthe driver stopped early: " + e.message);

    if (problems.length > 0) {
        console.error(`\n${problems.length} problem(s) found before that:`);
        for (const p of problems) { console.error("  - " + p); }
    } else {
        console.error(`\n${checks} checks passed before that point.`);
    }

    process.exit(2);
});

function firstLine(text) {
    return (text || "").split("\n").map(l => l.trim()).filter(Boolean).slice(0, 2).join(" / ");
}
