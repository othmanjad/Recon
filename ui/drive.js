const { chromium } = require("playwright");
const path = "file://" + __dirname + "/";
const OUT = process.env.SHOTS_DIR || (__dirname + "/.shots");
require("fs").mkdirSync(OUT, { recursive: true });

(async () => {
    /* CHROMIUM_PATH lets this run against a pre-installed browser (CI images
       often ship one that does not match Playwright's expected build). */
    const launchOpts = process.env.CHROMIUM_PATH ? { executablePath: process.env.CHROMIUM_PATH } : {};
    const browser = await chromium.launch(launchOpts);
    const problems = [];

    async function open(page, file) {
        const errs = [];
        page.on("console", m => { if (m.type() === "error") errs.push("console: " + m.text()); });
        page.on("pageerror", e => errs.push("pageerror: " + e.message));
        page.on("requestfailed", r => errs.push("requestfailed: " + r.url().split("/").pop()));
        const resp = await page.goto(path + file, { waitUntil: "load" });
        await page.waitForTimeout(400);
        return errs;
    }

    const PAGES = ["index.html","datasets.html","rules.html","exceptions.html",
                   "counterparties.html","settings.html"];

    for (const file of PAGES) {
        const ctx = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
        const page = await ctx.newPage();
        const errs = await open(page, file);

        // every page must have rendered its shell
        const navCount = await page.locator("#rc-sidebar .nav-link").count();
        if (navCount !== 6) errs.push(`sidebar rendered ${navCount} links, expected 6`);
        const brand = await page.locator(".rc-brand").count();
        if (brand !== 1) errs.push("brand missing");

        // no page may leave an empty main table body
        const emptyBodies = await page.locator("tbody:empty").count();
        if (emptyBodies > 0) errs.push(`${emptyBodies} empty tbody left unrendered`);

        await page.screenshot({ path: `${OUT}/${file.replace(".html","")}.png`, fullPage: true });
        if (errs.length) problems.push({ file, errs });
        await ctx.close();
    }

    // ---- interactions -------------------------------------------------
    const ctx = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
    const page = await ctx.newPage();
    const errs = await open(page, "index.html");

    // dashboard: selecting a run must repaint the distribution panel
    await page.locator('tr[data-run="4469"] .btn-view').click();
    await page.waitForTimeout(250);
    let distRun = await page.locator("#dist-run").innerText();
    if (!distRun.includes("4469")) errs.push(`run selection did not repaint distribution (got "${distRun}")`);

    // filter to Failed must leave exactly run 4467
    await page.selectOption("#f-status", "Failed");
    await page.waitForTimeout(250);
    const rowCount = await page.locator("#run-rows tr.rc-run-row").count();
    if (rowCount !== 1) errs.push(`status filter produced ${rowCount} rows, expected 1`);
    await page.screenshot({ path: `${OUT}/dash-failed-filter.png`, fullPage: true });
    if (errs.length) problems.push({ file: "index.html (interaction)", errs });

    // ---- rule builder -------------------------------------------------
    const rerrs = await open(page, "rules.html");
    const passCount = await page.locator(".rc-pass").count();
    if (passCount !== 4) rerrs.push(`expected 4 passes, got ${passCount}`);

    // valid tree
    await page.locator("#btn-sample-ok").click();
    await page.waitForTimeout(200);
    if (await page.locator("#ct-result .alert-success").count() !== 1)
        rerrs.push("valid condition tree was not accepted in the UI");

    // injection payload must be visibly rejected
    await page.locator("#btn-sample-bad").click();
    await page.waitForTimeout(200);
    if (await page.locator("#ct-result .alert-danger").count() !== 1)
        rerrs.push("rejected condition tree did not show an error");
    const errText = await page.locator("#ct-result").innerText();
    if (!errText.includes("not in the field registry")) rerrs.push("injection rejection reason not shown");
    if (!errText.includes("not marked matchable")) rerrs.push("non-matchable rejection reason not shown");
    await page.screenshot({ path: `${OUT}/rules-rejected.png`, fullPage: true });

    // change pass 1 to a non-indexable comparison -> warning must appear
    await page.locator('.rc-pass[data-pass="0"] .c-cmp').first().selectOption("Contains");
    await page.waitForTimeout(250);
    if (await page.locator("#order-warning").count() !== 1)
        rerrs.push("pass-order warning did not appear for a non-sargable pass-1 comparison");
    if (await page.locator(".rc-warn-nonsargable").count() < 1)
        rerrs.push("non-sargable condition row was not highlighted");
    await page.screenshot({ path: `${OUT}/rules-nonsargable.png`, fullPage: true });

    // add + remove a pass
    await page.locator("#btn-add-pass").click();
    await page.waitForTimeout(200);
    if (await page.locator(".rc-pass").count() !== 5) rerrs.push("add pass did not work");
    await page.locator('.rc-pass[data-pass="4"] .btn-del-pass').click();
    await page.waitForTimeout(200);
    if (await page.locator(".rc-pass").count() !== 4) rerrs.push("delete pass did not work");

    // dry run
    await page.locator("#btn-dryrun").click();
    await page.waitForTimeout(250);
    if (await page.locator("#dryrun-card[hidden]").count() !== 0) rerrs.push("dry-run panel stayed hidden");
    if (rerrs.length) problems.push({ file: "rules.html (interaction)", errs: rerrs });

    // ---- datasets -----------------------------------------------------
    const derrs = await open(page, "datasets.html");
    if (await page.locator("#fr-rows tr").count() !== 9)
        derrs.push("CliQ registry did not render 9 fields");
    if (await page.locator("#fr-validation .alert-success").count() !== 1)
        derrs.push("CliQ dataset should validate as ready to activate");
    // dataset 3 has no fields -> must show the activation blocker
    await page.locator('#ds-list [data-ds="3"]').click();
    await page.waitForTimeout(250);
    if (await page.locator("#fr-validation .alert-warning").count() !== 1)
        derrs.push("empty dataset should be blocked from activation");
    const warnText = await page.locator("#fr-validation").innerText();
    if (!warnText.includes("Reference")) derrs.push("missing-role list not shown");
    await page.screenshot({ path: `${OUT}/datasets-blocked.png`, fullPage: true });
    if (derrs.length) problems.push({ file: "datasets.html (interaction)", errs: derrs });

    // ---- exceptions ---------------------------------------------------
    const xerrs = await open(page, "exceptions.html");
    const allRows = await page.locator("#ex-rows tr").count();
    await page.selectOption("#f-exstatus", "Open");
    await page.waitForTimeout(250);
    const openRows = await page.locator("#ex-rows tr").count();
    if (!(openRows < allRows)) xerrs.push("status filter did not narrow the list");
    await page.selectOption("#f-age", "8");
    await page.waitForTimeout(250);
    const agedRows = await page.locator("#ex-rows tr").count();
    if (agedRows !== 1) xerrs.push(`aging filter produced ${agedRows} rows, expected 1`);
    await page.screenshot({ path: `${OUT}/exceptions-aged.png`, fullPage: true });
    if (xerrs.length) problems.push({ file: "exceptions.html (interaction)", errs: xerrs });

    // ---- responsive ---------------------------------------------------
    const mctx = await browser.newContext({ viewport: { width: 390, height: 844 } });
    const mp = await mctx.newPage();
    const merrs = await open(mp, "index.html");
    const bodyScrollW = await mp.evaluate(() => document.body.scrollWidth);
    const winW = await mp.evaluate(() => window.innerWidth);
    if (bodyScrollW > winW + 1) merrs.push(`horizontal overflow at 390px: body ${bodyScrollW} > ${winW}`);
    if (await mp.locator(".rc-sidebar-wrap").isVisible()) merrs.push("sidebar not collapsed at phone width");
    await mp.screenshot({ path: `${OUT}/mobile-dashboard.png`, fullPage: true });
    // offcanvas must open
    await mp.locator('[data-bs-target="#rc-offcanvas"]').click();
    await mp.waitForTimeout(500);
    if (await mp.locator("#rc-offcanvas.show").count() !== 1) merrs.push("offcanvas menu did not open");
    await mp.screenshot({ path: `${OUT}/mobile-menu.png` });
    if (merrs.length) problems.push({ file: "mobile", errs: merrs });

    await browser.close();

    if (problems.length === 0) {
        console.log("ALL CHECKS PASSED — 6 pages, interactions, and phone width, no console errors.");
    } else {
        console.log("PROBLEMS:");
        for (const p of problems) {
            console.log("\n  " + p.file);
            for (const e of p.errs) console.log("    - " + e);
        }
        process.exit(1);
    }
})();
