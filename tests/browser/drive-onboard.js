/* =====================================================================
   The Phase 3 exit criterion, as a test: onboard a counterparty with
   nothing but the portal.

       ./demo/run-portal.sh --background
       node tests/browser/drive-onboard.js

   The design says the measure of whether this platform was built is
   whether Operations can onboard a SECOND counterparty unassisted — and
   that if it needs a developer, what was built is a CliQ tool with extra
   tables. A test cannot settle the "unassisted" part, which is about a
   person. It can settle the part that is about the software: that every
   step of the sequence exists as a screen and that the result actually
   reconciles.

   So this driver creates a counterparty, two datasets, their field
   registries, a CSV format and its mappings for each side, a definition,
   a matching pass, activates all of it, uploads two files it writes
   itself, and asserts the run matched what those files were built to
   match. No SQL anywhere.

   Then it does the same day's work the way a scheduled night does it:
   configures folder acquisition, gives the format a file-name pattern,
   drops the day's file — and the wrong day's beside it — into the watched
   directory, and triggers a run with nothing uploaded at all.
   ===================================================================== */

const { chromium } = require("playwright");
const fs = require("fs");
const os = require("os");
const path = require("path");

const BASE = process.env.PORTAL_URL || "http://127.0.0.1:5080";
const OPERATOR = process.env.RECON_OPERATOR || "cfg.omar";
const OUT = process.env.SHOTS_DIR || path.join(__dirname, ".shots");
fs.mkdirSync(OUT, { recursive: true });

/* A code unique to this run, so re-running does not collide with the
   counterparty the last run created. */
const TAG = "ZZ" + Date.now().toString().slice(-6);
const BUSINESS_DATE = "2026-09-13";

/* A second business date, reconciled from a watched folder rather than from
   an upload. A different date is what makes the {yyyyMMdd} substitution in
   the file-name pattern testable: the wrong day's file is in the same folder
   and must not be picked. */
const ACQUIRE_DATE = "2026-09-14";
const ACQUIRE_COMPACT = ACQUIRE_DATE.replace(/-/g, "");

const problems = [];
let checks = 0;

/* Progress is written synchronously to a file rather than through
   console.log: Node buffers stdout when it is redirected, so a driver that
   hangs prints nothing at all and there is no way to see how far it got. */
const TRACE = process.env.TRACE_FILE || path.join(OUT, "onboard-trace.log");
fs.writeFileSync(TRACE, "");

function step(what) {
    fs.appendFileSync(TRACE, `${new Date().toISOString()}  ${what}\n`);
}

function check(condition, message) {
    checks++;
    if (!condition) { problems.push(message); }
}

async function submit(page, locator, timeout = 60000) {
    step("submit: " + (await locator.getAttribute("class") || "?"));

    await Promise.all([
        page.waitForNavigation({ waitUntil: "load", timeout }),
        locator.click(),
    ]);
}

/* Four universal roles a dataset cannot be activated without — control
   totals and fee logic read them — plus Date, which is OPTIONAL: a dataset
   without it has every row stamped with the business date, which is right for
   a summary feed. These datasets are transaction feeds, so they map it. */
const LEFT_FIELDS = [
    { code: "REF", label: "Ledger Reference", type: "String", role: "Reference", slot: "Text1", order: 1, indexed: true, required: true },
    { code: "AMT", label: "Amount", type: "Integer", role: "Amount", slot: "Num1", order: 2, required: true },
    { code: "CCY", label: "Currency", type: "String", role: "Currency", slot: "Text2", order: 3, required: true },
    { code: "WHEN", label: "Posted", type: "DateTime", role: "Date", slot: "Date1", order: 4, required: true },
    { code: "DIR", label: "Direction", type: "String", role: "Direction", slot: "Text3", order: 5, required: true },
    { code: "STATUS", label: "Status", type: "String", role: "Status", slot: "Text4", order: 6 },
];

/* The right-hand side deliberately has NO Date-role field, because that role
   is optional: every row is stamped with the business date instead. A summary
   feed is the case it exists for — one row describing a whole session has no
   transaction date of its own — and this driver is where that is proved to
   activate and reconcile rather than only to compile. The left side keeps its
   date, so both paths run in the same reconciliation. */
const RIGHT_FIELDS = [
    { code: "STMT_REF", label: "Statement Reference", type: "String", role: "Reference", slot: "Text1", order: 1, indexed: true, required: true },
    { code: "STMT_AMT", label: "Amount", type: "Integer", role: "Amount", slot: "Num1", order: 2, required: true },
    { code: "STMT_CCY", label: "Currency", type: "String", role: "Currency", slot: "Text2", order: 3, required: true },
    { code: "STMT_DIR", label: "Direction", type: "String", role: "Direction", slot: "Text3", order: 5, required: true },
];

/* Two files that agree on two references and disagree on one each way,
   so the run has something to match and something to leave open. The
   fourth left-hand row is rejected by the scheme: the exclusion rule takes
   it out before pass 1, and it must NOT become an exception — a rejected
   transaction is not a break. */
const LEFT_CSV = csvFor(BUSINESS_DATE).left;
const RIGHT_CSV = csvFor(BUSINESS_DATE).right;

/* The same shape of data for the acquisition date: the counts the run is
   asserted on are the counts these rows produce. */
function csvFor(date) {
    return {
        left: [
            "reference,amount,currency,posted,direction,status",
            `${TAG}-0001,1500,JOD,${date} 09:15:00,Inward,ACSC`,
            `${TAG}-0002,2750,JOD,${date} 10:30:00,Outward,ACSC`,
            `${TAG}-0009,400,JOD,${date} 11:00:00,Inward,ACSC`,
            `${TAG}-0099,650,JOD,${date} 11:30:00,Inward,RJCT`,
        ].join("\n") + "\n",
        right: [
            "ref,amount,ccy,value_date,dir",
            `${TAG}-0001,1500,JOD,${date} 09:16:00,Inward`,
            `${TAG}-0002,2750,JOD,${date} 10:31:00,Outward`,
            `${TAG}-0007,900,JOD,${date} 12:00:00,Inward`,
        ].join("\n") + "\n",
    };
}

(async () => {
    const launch = process.env.CHROMIUM_PATH ? { executablePath: process.env.CHROMIUM_PATH } : {};
    const browser = await chromium.launch(launch);
    const context = await browser.newContext({ viewport: { width: 1500, height: 1100 } });
    const page = await context.newPage();

    page.on("dialog", d => d.accept());
    page.on("pageerror", e => problems.push("pageerror: " + e.message));
    page.on("console", m => { if (m.type() === "error") { problems.push("console: " + m.text()); } });
    page.on("response", r => {
        if (r.status() >= 500) { problems.push(`HTTP ${r.status()} for ${r.url()}`); }
    });

    await page.goto(BASE + "/account/signin", { waitUntil: "load" });
    await page.fill("#userName", OPERATOR);
    await submit(page, page.locator('button[type="submit"]'), 30000);

    // =================================================================
    // 1. A counterparty.
    // =================================================================
    step("phase 1: counterparty");
    await page.goto(BASE + "/counterparties?blank=true", { waitUntil: "load" });

    check(await page.locator('input[name="counterpartyId"]').count() === 0,
        "the counterparty form carried an id, so it would have edited an existing one");

    await page.fill("#code", TAG + "_BANK");
    await page.fill("#name", "Test bank " + TAG);
    await page.fill("#description", "Created by drive-onboard.js");
    await submit(page, page.locator('form[action*="/Counterparties/SaveCounterparty"] button[type="submit"]'), 60000);

    let body = await page.locator("body").innerText();
    check(new RegExp(TAG + "_BANK").test(body), "the new counterparty was not confirmed");
    check(/granted Configure access to it/.test(body) || /saved\./.test(body),
        "creating a counterparty said nothing about access to it");

    // =================================================================
    // 2. Two datasets, with their field registries.
    // =================================================================
    const leftCode = TAG + "_LEDGER";
    const rightCode = TAG + "_STMT";

    step("phase 2: datasets");
    await createDataset(leftCode, "Bank ledger " + TAG);
    await createDataset(rightCode, "Bank statement " + TAG);

    const leftId = await datasetIdFor(leftCode);
    const rightId = await datasetIdFor(rightCode);

    check(leftId !== null && rightId !== null,
        `both datasets should exist (left=${leftId}, right=${rightId})`);

    step("phase 2b: fields");
    for (const field of LEFT_FIELDS) { await addField(leftId, field); }
    for (const field of RIGHT_FIELDS) { await addField(rightId, field); }

    /* The roles are in place, but there is still no file format — and a
       dataset with no layout cannot read a file at all. That used to activate
       happily and fail at the first upload with "no file format effective
       on ...", which is a configuration mistake discovered at run time. */
    await page.goto(`${BASE}/datasets?id=${leftId}`, { waitUntil: "load" });
    body = await page.locator("body").innerText();

    check(!/missing universal role/.test(body),
        `the left registry is incomplete: ${activationText(body)}`);
    check(/no file format/.test(body),
        "the screen did not say that a dataset with no file format cannot load a file");
    check(!/can be activated/.test(body),
        "a dataset with no file format claimed it was ready to activate");

    check(LEFT_FIELDS.every(f => body.includes(f.code)),
        "not every field was saved into the left registry");

    // And the button agrees with the panel.
    const earlyToggle = page.locator('form[action*="/Datasets/Activate"] button').first();
    if ((await earlyToggle.innerText()).trim() === "Activate") {
        await submit(page, earlyToggle, 60000);
        body = await page.locator("body").innerText();

        check(/Cannot activate/.test(body) && /no file format/.test(body),
            `a dataset with no file format was activated anyway: ${firstAlert(body)}`);
    }

    await page.goto(`${BASE}/datasets?id=${leftId}`, { waitUntil: "load" });
    await shot(page, "onboard-1-registry");

    /* The right side has no Date-role field, and that must be an ADVISORY, not
       a block: the gate refusing it would be the platform deciding a business
       question it does not own. The missing format is the only thing standing
       in its way here. */
    await page.goto(`${BASE}/datasets?id=${rightId}`, { waitUntil: "load" });
    body = await page.locator("body").innerText();

    check(!/missing universal role/.test(body),
        `a dataset with no Date-role field was told a role was missing: ${activationText(body)}`);
    check(/no Date-role field/.test(body),
        "the screen did not say what a dataset without a Date-role field gives up");
    check(/business date/.test(body),
        "the advisory did not say that rows are stamped with the business date");
    check(/Date \(optional\)/.test(body),
        "the role badges do not mark Date as optional");

    // =================================================================
    // 3. A CSV format and its mappings, per side.
    // =================================================================
    step("phase 3: formats");
    await createFormat(leftId);
    await createFormat(rightId);

    const leftFormatId = await formatIdFor(leftId);
    const rightFormatId = await formatIdFor(rightId);

    const leftMappings = [
        ["REF", "reference", ""],
        ["AMT", "amount", ""],
        ["CCY", "currency", ""],
        ["WHEN", "posted", "yyyy-MM-dd HH:mm:ss"],
        ["DIR", "direction", ""],
        ["STATUS", "status", ""],
    ];

    // No STMT_WHEN: the column stays in the file and is simply not read.
    const rightMappings = [
        ["STMT_REF", "ref", ""],
        ["STMT_AMT", "amount", ""],
        ["STMT_CCY", "ccy", ""],
        ["STMT_DIR", "dir", ""],
    ];

    for (const [field, source, format] of leftMappings) {
        await addMapping(leftId, leftFormatId, field, source, format);
    }

    for (const [field, source, format] of rightMappings) {
        await addMapping(rightId, rightFormatId, field, source, format);
    }

    await page.goto(`${BASE}/datasets?id=${leftId}&formatId=${leftFormatId}`, { waitUntil: "load" });
    body = await page.locator("body").innerText();

    check(/6 of 6 registry field\(s\) mapped/.test(body),
        `the left mappings are incomplete: ${(body.match(/\d+ of \d+ registry field\(s\) mapped/) || ["(not reported)"])[0]}`);

    // A transform chain the loader would refuse must be refused here.
    await page.goto(`${BASE}/datasets?id=${leftId}&formatId=${leftFormatId}`, { waitUntil: "load" });
    await page.selectOption("#mapFieldCode", "REF");
    await page.fill("#sourcePath", "reference");
    await page.fill("#transformChainJson", '[{"op":"NoSuchOperation"}]');
    await submit(page, page.locator('form[action*="/Datasets/SaveMapping"] button[type="submit"]'), 60000);

    body = await page.locator("body").innerText();
    check(/transform chain was rejected/.test(body),
        "an unknown transform operation was accepted");
    check(/unknown transform/.test(body),
        "the refusal does not say what was wrong with the transform chain");

    await shot(page, "onboard-2-mappings");

    // =================================================================
    // 4. Activate both datasets — now that each has a layout to read its
    //    file with, the gate lifts.
    // =================================================================
    for (const id of [leftId, rightId]) {
        await page.goto(`${BASE}/datasets?id=${id}`, { waitUntil: "load" });
        const toggle = page.locator('form[action*="/Datasets/Activate"] button').first();

        if ((await toggle.innerText()).trim() === "Activate") {
            await submit(page, toggle, 60000);
            check(/activated\./.test(await page.locator("body").innerText()),
                `activating dataset ${id} was refused once it had a format and mappings`);
        }
    }

    // =================================================================
    // 5. A definition pairing them, and a pass.
    // =================================================================
    const counterpartyId = await counterpartyIdFor(TAG + "_BANK");
    await page.goto(`${BASE}/counterparties?id=${counterpartyId}`, { waitUntil: "load" });

    await page.selectOption("#leftDatasetId", await optionValue("#leftDatasetId", leftCode));
    await page.selectOption("#rightDatasetId", await optionValue("#rightDatasetId", rightCode));
    await page.fill("#defCode", TAG + "_RECON");
    await page.fill("#defName", "Ledger against statement " + TAG);
    await submit(page, page.locator('form[action*="/Counterparties/SaveDefinition"] button[type="submit"]'), 60000);

    body = await page.locator("body").innerText();
    check(new RegExp(TAG + "_RECON").test(body), "the definition was not created");
    check(/inactive/.test(body), "a new definition should be created inactive");

    const definitionId = await definitionIdFor(TAG + "_RECON");
    check(definitionId !== null, "the new definition has no id");

    // The rule builder: one pass, reference and amount.
    await page.goto(`${BASE}/rules?id=${definitionId}`, { waitUntil: "load" });

    body = await page.locator("body").innerText();
    check(/has no active pass/.test(body),
        "a definition with no passes did not say so on the activation panel");

    const newPass = page.locator("#rc-new-pass");
    await newPass.locator('input[name="ruleCode"]').fill("P1_REF");
    await newPass.locator('input[name="name"]').fill("Reference and amount");

    await newPass.locator(".rc-add-cond").click();
    await page.waitForTimeout(200);
    await newPass.locator(".rc-add-cond").click();
    await page.waitForTimeout(200);

    const rows = newPass.locator(".rc-cond-row");
    check(await rows.count() === 2, "two condition rows were not added");

    await rows.nth(0).locator(".rc-left").selectOption("REF");
    await rows.nth(0).locator(".rc-right").selectOption("STMT_REF");
    await rows.nth(0).locator(".rc-cmp").selectOption("Exact");

    await rows.nth(1).locator(".rc-left").selectOption("AMT");
    await rows.nth(1).locator(".rc-right").selectOption("STMT_AMT");
    await rows.nth(1).locator(".rc-cmp").selectOption("NumericExact");

    await submit(page, newPass.locator('button:has-text("Add pass")'), 60000);

    body = await page.locator("body").innerText();
    check(/saved with 2 condition\(s\)/.test(body),
        `the pass was not saved with both conditions: ${firstAlert(body)}`);
    check(/This definition can run/.test(body),
        `the definition still cannot run: ${activationText(body)}`);

    await shot(page, "onboard-3-rules");

    // =================================================================
    // 5b. The three rule sets: what never matches, what an unmatched row
    //     is called, and whether the money adds up.
    // =================================================================
    step("phase 5b: rule sets");
    await page.goto(`${BASE}/rules?id=${definitionId}`, { waitUntil: "load" });

    /* Built from dropdowns, which is the point: an operator configuring an
       exclusion used to have to type
       {"op":"and","items":[{"field":"STATUS","cmp":"eq","value":"RJCT"}]}
       by hand, in a product whose claim is that they never write code. */
    await page.fill("#exName", "Rejected by the scheme");
    await page.fill("#exReason", "REJECTED");
    await buildCondition("ex", "STATUS", "eq", "RJCT");

    const exJson = await page.locator("#exJson").evaluate(el => el.value);
    check(exJson === '{"op":"and","items":[{"field":"STATUS","cmp":"eq","value":"RJCT"}]}',
        `the builder wrote the wrong condition: ${exJson}`);

    // And it says in words what it is about to save.
    check(/STATUS يساوي RJCT/.test(await page.locator("#exBuilder").innerText()),
        "the builder does not say in words what the condition means");

    await submit(page, page.locator('form[action*="/Rules/SaveExclusion"] button[type="submit"]'), 60000);

    body = await page.locator("body").innerText();
    check(/Exclusion 'Rejected by the scheme' saved/.test(body),
        `the exclusion was not saved: ${firstAlert(body)}`);

    /* The builder cannot offer a field outside the registry — that is the
       security boundary drawn as a dropdown. But the server's gate must still
       hold for anything that does not come through it, so the advanced JSON
       box is where that is proved. */
    await page.goto(`${BASE}/rules?id=${definitionId}`, { waitUntil: "load" });

    const offered = await page.locator("#exBuilder .rc-cb-field option")
        .evaluateAll(options => options.map(o => o.value));

    check(!offered.includes("NO_SUCH_FIELD"),
        "the builder offered a field that is not in the registry");
    check(offered.includes("STATUS"),
        `the builder does not offer the left dataset's fields: ${offered.join(", ")}`);

    await page.fill("#exName", "Nonsense");
    await page.fill("#exReason", "NOPE");
    await page.locator("#exBuilder").locator("xpath=..").locator("details summary").first().click();
    await page.fill("#exJsonRaw",
        '{"op":"and","items":[{"field":"NO_SUCH_FIELD","cmp":"eq","value":"x"}]}');

    await submit(page, page.locator('form[action*="/Rules/SaveExclusion"] button[type="submit"]'), 60000);

    body = await page.locator("body").innerText();
    check(/exclusion condition was rejected/.test(body),
        "an exclusion naming a field outside the registry was accepted");

    // A classification for each side, so the unmatched rows get names.
    await addClassification("MISSING_IN_STATEMENT", "In the ledger, not in the statement", "Left", 1,
        "REF", "isnotnull");

    await addClassification("MISSING_IN_LEDGER", "In the statement, not in the ledger", "Right", 2,
        "STMT_REF", "isnotnull");

    /* "Both" has to resolve against both registries, and these two name
       nothing alike — so the builder offers only what they share, which here
       is nothing. The server's refusal is proved through the JSON box. */
    await page.goto(`${BASE}/rules?id=${definitionId}`, { waitUntil: "load" });
    await page.selectOption("#clSide", "Both");

    const shared = await page.locator("#clBuilder .rc-cb-field option")
        .evaluateAll(options => options.map(o => o.value));

    check(!shared.includes("REF") && !shared.includes("STMT_REF"),
        `a Both-sided rule was offered one side's fields: ${shared.join(", ")}`);
    check(/المشتركة بين الطرفين/.test(await page.locator("#rc-classification-form").innerText()),
        "the screen does not explain why the Both field list is narrower");

    /* Nothing to offer is a state the builder has to say out loud. It used to
       draw an empty dropdown, which sends a blank condition — and the column
       is NOT NULL, so the DATABASE answered: "Cannot insert the value NULL
       into column 'ConditionJson'". */
    const dead = await page.locator("#clBuilder").innerText();
    check(/لا يتشاركان/.test(dead),
        `the builder did not say WHY it has nothing to offer: ${dead}`);
    check(dead.includes("REF") && dead.includes("STMT_REF"),
        "the message does not list what each side actually has");
    check(!await page.locator("#clBuilder .rc-cb-head").isVisible(),
        "the ALL/ANY head is still on screen with no condition row under it");

    /* And the way out is a button, not advice: it moves the side picker, and
       the builder comes back with that side's fields. */
    await page.click('#clBuilder .rc-cb-pick[data-side="Left"]');
    check(await page.locator("#clSide").inputValue() === "Left",
        "the way-out button did not switch the side");

    const offeredAfter = await page.locator("#clBuilder .rc-cb-field option")
        .evaluateAll(options => options.map(o => o.value));
    check(offeredAfter.includes("REF"),
        `switching to Left did not bring the left side's fields back: ${offeredAfter.join(", ")}`);

    await page.selectOption("#clSide", "Both");

    /* Saving with no condition is refused twice, and both matter. The browser
       refuses it on the spot, because a round trip to be told what the screen
       already knew costs the person the form they had filled in. */
    await page.fill("#clCode", "NO_CONDITION");
    await page.fill("#clName", "Saved with nothing to match on");
    await page.fill("#clSeq", "8");
    await page.click('form[action*="/Rules/SaveClassification"] button[type="submit"]');
    await page.waitForTimeout(400);

    check(await page.locator("#clBuilder .rc-cb-blocked").count() === 1,
        "the browser submitted a classification with no condition");
    check(await page.locator("#clCode").inputValue() === "NO_CONDITION",
        "the blocked submit lost what had already been typed");

    // And the server, through a POST the browser's rule never sees — which is
    // the one that counts: the column is NOT NULL, and without this gate the
    // DATABASE answered with "Cannot insert the value NULL into column
    // 'ConditionJson'" after the form was gone.
    const clToken = await page
        .locator('form[action*="/Rules/SaveClassification"] input[name="__RequestVerificationToken"]')
        .getAttribute("value");

    const clRefused = await context.request.post(`${BASE}/Rules/SaveClassification`, {
        form: {
            __RequestVerificationToken: clToken ?? "",
            definitionId: String(definitionId),
            exceptionCode: "NO_CONDITION",
            displayName: "Saved with nothing to match on",
            appliesToSide: "Left",
            conditionJson: "",
            actionType: "ReportOnly",
            severity: "Normal",
            sequence: "8",
            isActive: "true",
        },
        maxRedirects: 0,
    });

    check(clRefused.status() === 302,
        `a crafted POST answered ${clRefused.status()} rather than redirecting with a message`);

    await page.goto(`${BASE}/rules?id=${definitionId}`, { waitUntil: "load" });
    body = await page.locator("body").innerText();

    check(/A classification needs a condition/.test(body),
        `the server accepted a classification with no condition: ${firstAlert(body)}`);
    check(!/Cannot insert the value NULL/.test(body),
        "the database answered a blank condition instead of the screen");
    check(!/NO_CONDITION/.test(body), "the refused classification was stored anyway");

    // The same two gates on the exclusion form.
    await page.fill("#exName", "No condition either");
    await page.fill("#exReason", "NONE");
    await page.locator("#exBuilder .rc-cb-row").evaluateAll(rows => rows.forEach(r => r.remove()));
    await page.click('form[action*="/Rules/SaveExclusion"] button[type="submit"]');
    await page.waitForTimeout(400);

    check(await page.locator("#exBuilder .rc-cb-blocked").count() === 1,
        "the browser submitted an exclusion with no condition");

    const leftDatasetId = await page.locator("#exDataset option").first().getAttribute("value");

    const exToken = await page
        .locator('form[action*="/Rules/SaveExclusion"] input[name="__RequestVerificationToken"]')
        .getAttribute("value");

    const exRefused = await context.request.post(`${BASE}/Rules/SaveExclusion`, {
        form: {
            __RequestVerificationToken: exToken ?? "",
            definitionId: String(definitionId),
            datasetId: String(leftDatasetId),
            name: "No condition either",
            reasonCode: "NONE",
            conditionJson: "",
            isActive: "true",
        },
        maxRedirects: 0,
    });

    check(exRefused.status() === 302,
        `a crafted POST answered ${exRefused.status()} rather than redirecting with a message`);

    await page.goto(`${BASE}/rules?id=${definitionId}`, { waitUntil: "load" });
    body = await page.locator("body").innerText();

    check(/An exclusion needs a condition/.test(body),
        `the server accepted an exclusion with no condition: ${firstAlert(body)}`);

    await page.goto(`${BASE}/rules?id=${definitionId}`, { waitUntil: "load" });
    await page.selectOption("#clSide", "Both");

    await page.fill("#clCode", "BOTH_SIDES");
    await page.fill("#clName", "Applies to both");
    await page.fill("#clSeq", "9");
    await page.locator("#clBuilder").locator("xpath=..").locator("details summary").first().click();
    await page.fill("#clJsonRaw", '{"op":"and","items":[{"field":"REF","cmp":"isnotnull"}]}');
    await submit(page, page.locator('form[action*="/Rules/SaveClassification"] button[type="submit"]'), 60000);

    body = await page.locator("body").innerText();
    check(/must resolve against both registries/.test(body),
        "a Both-sided rule with a one-sided condition was accepted");

    // A control total: the matched value on each side must agree.
    await page.fill("#ctCode", "MATCHED_TOTAL");
    await page.fill("#ctName", "Matched value agrees");
    await page.selectOption("#functionA", "SumAmount");
    await page.selectOption("#sideA", "Left");
    await page.selectOption("#matchStatusA", "Matched");
    await page.selectOption("#functionB", "SumAmount");
    await page.selectOption("#sideB", "Right");
    await page.selectOption("#matchStatusB", "Matched");
    await submit(page, page.locator('form[action*="/Rules/SaveControlTotal"] button[type="submit"]'), 60000);

    body = await page.locator("body").innerText();
    check(/MATCHED_TOTAL saved, and it compiles/.test(body),
        `the control total was not saved: ${firstAlert(body)}`);
    check(/will FAIL the run/.test(body),
        "the screen did not say that a difference fails the run");

    /* A period-scoped check with no window is refused twice over, and both
       matter. The browser refuses it because the field becomes required when
       the scope is Period — so the form cannot even be submitted. The server
       refuses it too, which is the one that counts: the browser's rule is a
       courtesy and a crafted POST does not pass through it. */
    step("period check without a window");
    await page.fill("#ctCode", "PERIOD_NO_WINDOW");
    await page.fill("#ctName", "Period with no window");
    await page.selectOption("#ctScope", "Period");
    await page.fill("#ctPeriod", "");

    const blocked = await page.locator('form[action*="/Rules/SaveControlTotal"]').evaluate(form => {
        const invalid = Array.from(form.elements)
            .filter(e => e.willValidate && !e.checkValidity())
            .map(e => (e.name || e.id) + ": " + e.validationMessage);

        return { valid: form.checkValidity(), invalid };
    });

    check(!blocked.valid && blocked.invalid.some(m => m.startsWith("periodDays")),
        `the form did not require a window for a period-scoped check: ${JSON.stringify(blocked)}`);

    // And the server, through a POST the browser's rule never sees.
    const token = await page.locator('form[action*="/Rules/SaveControlTotal"] input[name="__RequestVerificationToken"]')
        .getAttribute("value");

    const refused = await context.request.post(`${BASE}/Rules/SaveControlTotal`, {
        form: {
            __RequestVerificationToken: token ?? "",
            definitionId: String(definitionId),
            checkCode: "PERIOD_NO_WINDOW",
            displayName: "Period with no window",
            functionA: "SumAmount", sourceTypeA: "RunAggregate", sideA: "Left",
            functionB: "SumAmount", sourceTypeB: "RunAggregate", sideB: "Right",
            scope: "Period",
            toleranceMinor: "0",
            failRunOnMismatch: "false",
            isActive: "true",
        },
        maxRedirects: 0,
    });

    check(refused.status() === 302,
        `a crafted POST answered ${refused.status()} rather than redirecting with a message`);

    await page.goto(`${BASE}/rules?id=${definitionId}`, { waitUntil: "load" });
    body = await page.locator("body").innerText();

    check(/needs a window in days/.test(body),
        "the server accepted a period-scoped check with no window");
    check(!/PERIOD_NO_WINDOW/.test(body),
        "the refused check was stored anyway");

    await shot(page, "onboard-3b-rule-sets");

    // =================================================================
    // 6. Activate the definition.
    // =================================================================
    await page.goto(`${BASE}/counterparties?id=${counterpartyId}`, { waitUntil: "load" });
    const definitionToggle = page.locator('form[action*="/Counterparties/ActivateDefinition"] button').first();

    await submit(page, definitionToggle, 60000);
    check(/activated\./.test(await page.locator("body").innerText()),
        "activating the definition was refused");

    // =================================================================
    // 7. Upload two files and reconcile them.
    // =================================================================
    step("phase 7: upload and run");
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), "recon-onboard-"));
    const leftPath = path.join(directory, `${leftCode}_${BUSINESS_DATE}.csv`);
    const rightPath = path.join(directory, `${rightCode}_${BUSINESS_DATE}.csv`);

    fs.writeFileSync(leftPath, LEFT_CSV);
    fs.writeFileSync(rightPath, RIGHT_CSV);

    await page.goto(BASE + "/runs", { waitUntil: "load" });
    await page.selectOption("#uploadDefinition", await optionValue("#uploadDefinition", TAG + "_RECON"));
    await page.fill("#uploadDate", BUSINESS_DATE);
    await page.fill("#uploadSession", "S1");
    await page.setInputFiles("#leftFile", leftPath);
    await page.setInputFiles("#rightFile", rightPath);

    await submit(page, page.locator('form[action*="/Runs/Upload"] button[type="submit"]'), 300000);

    body = await page.locator("body").innerText();

    check(/completed/i.test(body), `the onboarded run did not complete: ${firstAlert(body)}`);

    /* Two references agree on both sides, so four staged rows are matched
       — one pair is two rows — and one row each way is left open. Those
       numbers are what the two files were written to produce, and they
       are the whole point: the configuration built through the portal
       reconciles. */
    check(/4 matched/.test(body), `expected 4 matched rows: ${firstAlert(body)}`);
    check(/2 unmatched/.test(body), `expected 2 unmatched rows: ${firstAlert(body)}`);
    check(/0 ambiguous/.test(body), `expected no ambiguous rows: ${firstAlert(body)}`);
    check(/P1_REF/.test(body), "the run page does not show the pass that matched");

    // The excluded row is out of the working set and is not a break.
    check(/Exclude/.test(body), "the run page does not show the exclusion stage");
    check(/MATCHED_TOTAL/.test(body), "the run page does not show the control total");
    check(/Balanced/.test(body),
        `the matched totals did not agree, so the configuration is wrong somewhere: ${firstAlert(body)}`);

    await shot(page, "onboard-4-run");

    // The unmatched rows are now named by the classification rules, and the
    // EXCLUDED row is not among them: a rejected transaction is not a break.
    await page.goto(BASE + "/exceptions?definitionId=" + definitionId, { waitUntil: "load" });
    body = await page.locator("body").innerText();

    check(/MISSING_IN_STATEMENT/.test(body),
        "the left side's unmatched row was not classified");
    check(/MISSING_IN_LEDGER/.test(body),
        "the right side's unmatched row was not classified");
    check(!/REJECTED/.test(body),
        "the excluded row became an exception — a rejected transaction is not a break");

    const exceptionRows = await page.locator("table.rc-table tbody tr").count();
    check(exceptionRows === 2,
        `expected exactly two exceptions, one per side, and got ${exceptionRows}`);

    await shot(page, "onboard-5-exceptions");

    // =================================================================
    // 8. Acquisition: the same reconciliation again, from a watched
    //    folder, with nothing uploaded.
    // =================================================================
    step("phase 8: acquisition");

    /* The folder is read by the PORTAL's process. In the demo that process
       runs in a container with the repository mounted at /src, so the path
       the form stores is the server's view of the directory and the path
       this driver writes to is the host's. Both are overridable for a
       portal started some other way. */
    const inboxHost = process.env.ACQUIRE_HOST_ROOT
        || path.join(__dirname, ".shots", "inbox-" + TAG);
    const inboxServer = process.env.ACQUIRE_SERVER_ROOT
        || "/src/tests/browser/.shots/inbox-" + TAG;

    const leftInboxHost = path.join(inboxHost, "left");
    const rightInboxHost = path.join(inboxHost, "right");

    fs.mkdirSync(leftInboxHost, { recursive: true });
    fs.mkdirSync(rightInboxHost, { recursive: true });

    // Only the two methods that have a provider are offered. Sftp and Api
    // are in the database's list and implement nothing.
    await page.goto(`${BASE}/datasets?id=${leftId}`, { waitUntil: "load" });

    const methods = await page.locator("#rc-acq-method option")
        .evaluateAll(options => options.map(o => o.value));

    check(methods.length === 2 && methods.includes("Folder") && methods.includes("Manual"),
        `the acquisition method list should offer only Folder and Manual: ${methods.join(", ")}`);

    /* And the server refuses them too, through a POST the select never sees.
       A method that stored configuration and fetched nothing would look like
       coverage, which is worse than a blank. */
    const acqToken = await page
        .locator('form[action*="/Datasets/SaveAcquisition"] input[name="__RequestVerificationToken"]')
        .getAttribute("value");

    const sftp = await context.request.post(`${BASE}/Datasets/SaveAcquisition`, {
        form: {
            __RequestVerificationToken: acqToken ?? "",
            datasetId: String(leftId),
            method: "Sftp",
            storageRootPath: inboxServer,
            retryCount: "3",
            retryDelaySeconds: "60",
        },
        maxRedirects: 0,
    });

    check(sftp.status() === 302,
        `a crafted Sftp POST answered ${sftp.status()} rather than redirecting with a message`);

    await page.goto(`${BASE}/datasets?id=${leftId}`, { waitUntil: "load" });
    body = await page.locator("body").innerText();

    check(/Sftp has no provider/.test(body),
        `the server accepted an acquisition method with no provider: ${firstAlert(body)}`);
    check(/not configured: files are uploaded/.test(body),
        "the refused method was stored anyway");

    // A relative path is refused: the folder is read by the server's process,
    // wherever that happens to have been started.
    await page.selectOption("#rc-acq-method", "Folder");
    await page.fill("#rc-acq-root", "inbox");
    await submit(page, page.locator('form[action*="/Datasets/SaveAcquisition"] button[type="submit"]'), 60000);

    body = await page.locator("body").innerText();
    check(/is not an absolute path/.test(body),
        `a relative acquisition folder was accepted: ${firstAlert(body)}`);

    // Saved, but the format has no file-name pattern yet — so the screen
    // says what is still missing rather than looking finished.
    await saveAcquisition(leftId, path.posix.join(inboxServer, "left"));
    body = await page.locator("body").innerText();

    check(/no file-name pattern/.test(body),
        `saving a folder with no file-name pattern said nothing about it: ${firstAlert(body)}`);

    await saveAcquisition(rightId, path.posix.join(inboxServer, "right"));

    // The pattern, per side. {yyyyMMdd} is substituted before it is matched.
    await setFileNamePattern(leftId, leftFormatId, `^${leftCode}_{yyyyMMdd}.*\.csv$`);
    await setFileNamePattern(rightId, rightFormatId, `^${rightCode}_{yyyyMMdd}.*\.csv$`);

    // Nothing in the folder yet: the file-not-received condition, which must
    // be distinguishable from a session that is not due.
    await checkFolder(leftId);
    body = await page.locator("body").innerText();

    check(/no file in .* matches/.test(body),
        `an empty folder did not report the file as not received: ${firstAlert(body)}`);

    // Now the day's files, and the wrong day's beside them.
    const acquire = csvFor(ACQUIRE_DATE);

    fs.writeFileSync(
        path.join(leftInboxHost, `${leftCode}_${ACQUIRE_COMPACT}.csv`), acquire.left);
    fs.writeFileSync(
        path.join(rightInboxHost, `${rightCode}_${ACQUIRE_COMPACT}.csv`), acquire.right);

    // The decoy: same dataset, a different business date. If the date were
    // not substituted into the pattern this would be a second match and the
    // acquisition would refuse to guess between them.
    fs.writeFileSync(
        path.join(leftInboxHost, `${leftCode}_20250101.csv`), csvFor("2025-01-01").left);

    const arrivalsBefore = await arrivalCount(leftId);

    await checkFolder(leftId);
    body = await page.locator("body").innerText();

    check(new RegExp(`${leftCode}_${ACQUIRE_COMPACT}\.csv`).test(body)
        && /waiting in the folder/.test(body),
        `checking the folder did not find the day's file: ${firstAlert(body)}`);

    check(await arrivalCount(leftId) === arrivalsBefore,
        "checking the folder recorded an arrival — checking is not consuming");

    await shot(page, "onboard-6-acquisition");

    // The run, with nothing uploaded: the plain trigger, which is what the
    // scheduler does at 22:00.
    await page.goto(BASE + "/runs", { waitUntil: "load" });
    await page.selectOption("#definitionId", await optionValue("#definitionId", TAG + "_RECON"));
    await page.fill("#businessDate", ACQUIRE_DATE);
    await page.fill("#sessionRef", "S1");

    await submit(page, page.locator('form[action*="/Runs/Trigger"] button[type="submit"]'), 300000);

    body = await page.locator("body").innerText();

    check(/acquired/.test(body),
        `the run did not fetch the files itself: ${firstAlert(body)}`);
    check(new RegExp(`${leftCode}_${ACQUIRE_COMPACT}\.csv`).test(body),
        `the run did not name the file it acquired: ${firstAlert(body)}`);
    check(/completed/i.test(body),
        `the acquired run did not complete: ${firstAlert(body)}`);
    check(/4 matched/.test(body), `the acquired run: expected 4 matched: ${firstAlert(body)}`);
    check(/2 unmatched/.test(body), `the acquired run: expected 2 unmatched: ${firstAlert(body)}`);
    check(/0 ambiguous/.test(body), `the acquired run: expected 0 ambiguous: ${firstAlert(body)}`);

    await shot(page, "onboard-7-acquired-run");

    /* The file's life, not only its arrival: an operator asking "did last
       night's file load, and how many rows did it reject" is asking this
       table. */
    await page.goto(`${BASE}/datasets?id=${leftId}`, { waitUntil: "load" });
    const arrival = await page.locator("#rc-acquisition tbody tr").first().innerText();

    check(arrival.includes(`${leftCode}_${ACQUIRE_COMPACT}.csv`),
        `the acquired file is not the newest arrival: ${arrival.replace(/\n/g, " | ")}`);
    check(/Parsed/.test(arrival),
        `the acquired file was not recorded as parsed: ${arrival.replace(/\n/g, " | ")}`);
    check(/\b4\b/.test(arrival),
        `the acquired file's row count was not recorded: ${arrival.replace(/\n/g, " | ")}`);

    // The same content again is a duplicate, not a second arrival — and the
    // run is still allowed, because a retry after a fix is legitimate.
    await page.goto(BASE + "/runs", { waitUntil: "load" });
    await page.selectOption("#definitionId", await optionValue("#definitionId", TAG + "_RECON"));
    await page.fill("#businessDate", ACQUIRE_DATE);
    await page.fill("#sessionRef", "S2");

    await submit(page, page.locator('form[action*="/Runs/Trigger"] button[type="submit"]'), 300000);

    body = await page.locator("body").innerText();

    check(/same content as a file already received/.test(body),
        `the same file twice was not reported as a duplicate: ${firstAlert(body)}`);
    check(/completed/i.test(body),
        `the re-run over the duplicate did not complete: ${firstAlert(body)}`);

    // Two files matching the same pattern: refused rather than guessed at.
    fs.writeFileSync(
        path.join(leftInboxHost, `${leftCode}_${ACQUIRE_COMPACT}_late.csv`), acquire.left);

    await checkFolder(leftId);
    body = await page.locator("body").innerText();

    check(/2 files in .* match/.test(body),
        `two matching files were not refused: ${firstAlert(body)}`);
    check(/not something to guess at/.test(body),
        "the refusal did not say why it will not choose");

    fs.rmSync(path.join(leftInboxHost, `${leftCode}_${ACQUIRE_COMPACT}_late.csv`));

    // And it can be taken away again: the dataset goes back to uploads, which
    // is what a partner switching to sending files by hand looks like.
    await page.goto(`${BASE}/datasets?id=${leftId}`, { waitUntil: "load" });
    await submit(page, page.locator('form[action*="/Datasets/DeleteAcquisition"] button[type="submit"]'), 60000);

    body = await page.locator("body").innerText();

    check(/Acquisition removed/.test(body),
        `removing the acquisition was refused: ${firstAlert(body)}`);
    check(/not configured: files are uploaded/.test(body),
        "the acquisition card still claims a folder after the acquisition was removed");


    await context.close();
    await browser.close();

    console.log(`\n${checks} checks`);

    if (problems.length === 0) {
        console.log(
            `ALL GREEN — ${TAG}_BANK was onboarded through the portal alone, ` +
            "and its second day was reconciled from a watched folder");
        process.exit(0);
    }

    console.log(`\n${problems.length} problem(s):`);
    for (const p of problems) { console.log("  - " + p); }
    process.exit(1);

    // =================================================================
    // Helpers
    // =================================================================

    async function createDataset(code, name) {
        await page.goto(BASE + "/datasets?blank=true", { waitUntil: "load" });

        check(await page.locator('input[name="datasetId"]').count() === 0,
            `the dataset form carried an id, so ${code} would have edited an existing dataset`);

        await page.selectOption("#counterpartyId", await optionValue("#counterpartyId", TAG + "_BANK"));
        await page.fill("#dsCode", code);
        await page.fill("#dsName", name);
        await page.selectOption("#providerType", "File");
        await page.selectOption("#defaultCurrency", "JOD");

        await submit(page, page.locator('form[action*="/Datasets/SaveDataset"] button[type="submit"]'), 60000);

        const text = await page.locator("body").innerText();
        check(new RegExp(code + " created").test(text), `${code} was not created: ${firstAlert(text)}`);
    }

    async function addField(datasetId, field) {
        step("field " + field.code);
        await page.goto(`${BASE}/datasets?id=${datasetId}`, { waitUntil: "load" });

        await page.fill("#fieldCode", field.code);
        await page.fill("#displayLabel", field.label);
        await page.selectOption("#dataType", field.type);
        await page.selectOption("#fieldRole", field.role);
        await page.fill("#displayOrder", String(field.order));

        // The slot list is filtered by the field's type, so it is chosen
        // after the type.
        await page.selectOption("#storageSlot", field.slot);

        if (field.indexed) { await page.check("#isIndexed"); }
        if (field.required) { await page.check("#isRequired"); }

        await submit(page, page.locator('form[action*="/Datasets/SaveField"] button[type="submit"]'), 60000);

        const text = await page.locator("body").innerText();
        check(new RegExp("Field " + field.code + " added").test(text),
            `${field.code} was not added: ${firstAlert(text)}`);
    }

    async function createFormat(datasetId) {
        await page.goto(`${BASE}/datasets?id=${datasetId}`, { waitUntil: "load" });

        await page.selectOption("#formatType", "Csv");
        await page.fill("#delimiter", ",");
        await page.fill("#effectiveFrom", "2026-01-01");

        await submit(page, page.locator('form[action*="/Datasets/SaveFormat"] button[type="submit"]'), 60000);

        const text = await page.locator("body").innerText();
        check(/Format version \d+ created/.test(text),
            `the format for dataset ${datasetId} was not created: ${firstAlert(text)}`);
    }

    async function addMapping(datasetId, formatId, fieldCode, source, parseFormat) {
        step("mapping " + fieldCode);
        await page.goto(`${BASE}/datasets?id=${datasetId}&formatId=${formatId}`, { waitUntil: "load" });

        await page.selectOption("#mapFieldCode", fieldCode);
        await page.fill("#sourcePath", source);
        await page.fill("#parseFormat", parseFormat);

        await submit(page, page.locator('form[action*="/Datasets/SaveMapping"] button[type="submit"]'), 60000);

        const text = await page.locator("body").innerText();
        check(new RegExp(fieldCode + " ← " + source + " saved").test(text),
            `the mapping ${fieldCode} ← ${source} was not saved: ${firstAlert(text)}`);
    }

    /* Sets the last row of one of the two condition builders. The value box
       is disabled for isnull/isnotnull, which take none — so it is only
       filled when the operator has somewhere to put it. */
    async function buildCondition(prefix, field, cmp, value) {
        const row = `#${prefix}Builder .rc-cb-row:last-child`;

        await page.selectOption(`${row} .rc-cb-field`, field);
        await page.selectOption(`${row} .rc-cb-cmp`, cmp);

        if (value !== undefined) {
            await page.fill(`${row} .rc-cb-value`, value);
        }
    }

    async function addClassification(code, label, side, sequence, field, cmp, value) {
        step("classification " + code);
        await page.goto(`${BASE}/rules?id=${definitionId}`, { waitUntil: "load" });

        await page.fill("#clCode", code);
        await page.fill("#clName", label);
        await page.selectOption("#clSide", side);
        await page.fill("#clSeq", String(sequence));
        await buildCondition("cl", field, cmp, value);

        await submit(page, page.locator('form[action*="/Rules/SaveClassification"] button[type="submit"]'), 60000);

        const text = await page.locator("body").innerText();
        check(new RegExp("Classification " + code + " saved").test(text),
            `${code} was not saved: ${firstAlert(text)}`);
    }

    async function saveAcquisition(datasetId, serverRoot) {
        await page.goto(`${BASE}/datasets?id=${datasetId}`, { waitUntil: "load" });

        await page.selectOption("#rc-acq-method", "Folder");
        await page.fill("#rc-acq-root", serverRoot);

        await submit(page, page.locator('form[action*="/Datasets/SaveAcquisition"] button[type="submit"]'), 60000);
    }

    /* Editing the format in force, which is how the pattern gets there. The
       form is asserted to have been PREFILLED from the selected version
       first: a form showing defaults beside a dropdown reading "v1" is a
       form that invites saving the wrong thing. */
    async function setFileNamePattern(datasetId, formatId, pattern) {
        step("pattern for dataset " + datasetId);
        await page.goto(`${BASE}/datasets?id=${datasetId}&formatId=${formatId}`, { waitUntil: "load" });

        check(await page.inputValue("#delimiter") === ",",
            "the format form did not prefill the delimiter of the version being edited");
        check(await page.inputValue("#effectiveFrom") === "2026-01-01",
            "the format form did not prefill the effective date of the version being edited");

        await page.fill("#fileNamePattern", pattern);
        await submit(page, page.locator('form[action*="/Datasets/SaveFormat"] button[type="submit"]'), 60000);

        const text = await page.locator("body").innerText();
        check(/Format version \d+ saved/.test(text),
            `the file-name pattern was not saved for dataset ${datasetId}: ${firstAlert(text)}`);
    }

    async function checkFolder(datasetId) {
        await page.goto(`${BASE}/datasets?id=${datasetId}`, { waitUntil: "load" });
        await page.fill("#rc-acq-date", ACQUIRE_DATE);
        await page.fill("#rc-acq-session", "S1");

        await submit(page, page.locator('form[action*="/Datasets/CheckAcquisition"] button[type="submit"]'), 60000);
    }

    async function arrivalCount(datasetId) {
        await page.goto(`${BASE}/datasets?id=${datasetId}`, { waitUntil: "load" });

        const empty = await page.locator("#rc-acquisition tbody .rc-empty").count();
        return empty > 0 ? 0 : await page.locator("#rc-acquisition tbody tr").count();
    }

    async function optionValue(selector, label) {
        const value = await page.locator(selector).evaluate((select, wanted) => {
            const option = Array.from(select.options).find(o => o.textContent.includes(wanted));
            return option ? option.value : null;
        }, label);

        check(value !== null, `no option matching "${label}" in ${selector}`);
        return value ?? "";
    }

    async function datasetIdFor(code) {
        await page.goto(BASE + "/datasets", { waitUntil: "load" });
        return Number(await optionValue("select[name='id']", code)) || null;
    }

    async function formatIdFor(datasetId) {
        await page.goto(`${BASE}/datasets?id=${datasetId}`, { waitUntil: "load" });

        const href = await page.locator('a[href*="formatId="]').first().getAttribute("href");
        const match = /formatId=(\d+)/.exec(href ?? "");

        check(match !== null, `no format link for dataset ${datasetId}`);
        return match ? Number(match[1]) : 0;
    }

    async function counterpartyIdFor(code) {
        await page.goto(BASE + "/counterparties", { waitUntil: "load" });
        return Number(await optionValue("select[name='id']", code)) || null;
    }

    async function definitionIdFor(code) {
        await page.goto(BASE + "/rules", { waitUntil: "load" });
        return Number(await optionValue("select[name='id']", code)) || null;
    }

    async function shot(target, name) {
        await target.screenshot({ path: path.join(OUT, `${name}.png`), fullPage: true });
    }
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

function firstAlert(text) {
    return (text || "").split("\n").map(l => l.trim()).filter(Boolean).slice(2, 4).join(" / ");
}

function activationText(text) {
    const lines = (text || "").split("\n").map(l => l.trim());
    const at = lines.findIndex(l => /^Activation$/.test(l));
    return at >= 0 ? lines.slice(at + 1, at + 4).join(" / ") : "(no activation panel)";
}
