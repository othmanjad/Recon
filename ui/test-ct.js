/* Unit tests for the condition-tree validator: the security boundary. */
const { JSDOM } = require("jsdom");
const fs = require("fs");

const dom = new JSDOM(`<!doctype html><html><body></body></html>`, { runScripts: "outside-only" });
const w = dom.window;
w.eval(fs.readFileSync(require.resolve("jquery/dist/jquery.js"), "utf8"));
w.eval(fs.readFileSync(__dirname + "/js/mock-data.js", "utf8"));
w.eval(fs.readFileSync(__dirname + "/js/condition-tree.js", "utf8"));

const CT = w.ReconConditionTree;
const M = w.ReconMock;
const cliq = M.datasetById(1);

let pass = 0, fail = 0;
function check(name, cond) {
    if (cond) { pass++; console.log("  ok   " + name); }
    else { fail++; console.log("  FAIL " + name); }
}
function errs(tree, ds) { return CT.validate(tree, ds).errors; }

console.log("condition-tree validator");

// --- accepts a well-formed tree
check("valid flat tree accepted",
    CT.validate({ op: "and", items: [{ field: "REF_PRIMARY", cmp: "eq", value: "X" }] }, cliq).valid);

check("valid nested tree accepted",
    CT.validate({ op: "and", items: [
        { field: "STATUS", cmp: "ne", value: "RJCT" },
        { op: "or", items: [
            { field: "DIRECTION", cmp: "eq", value: "Inward" },
            { field: "AMOUNT", cmp: "gte", value: 1000 }]}
    ]}, cliq).valid);

// --- THE security checks
check("unknown field code rejected",
    errs({ op: "and", items: [{ field: "NOT_A_FIELD", cmp: "eq", value: 1 }] }, cliq)
        .some(e => e.indexOf("not in the field registry") !== -1));

check("SQL injection payload as a field code rejected",
    errs({ op: "and", items: [
        { field: "STATUS'; DROP TABLE stg.StagingTransaction--", cmp: "eq", value: "x" }] }, cliq)
        .some(e => e.indexOf("not in the field registry") !== -1));

check("non-matchable field rejected (CURRENCY is matchable=false)",
    errs({ op: "and", items: [{ field: "CURRENCY", cmp: "eq", value: "JOD" }] }, cliq)
        .some(e => e.indexOf("not marked matchable") !== -1));

check("matchable field accepted alongside", 
    CT.validate({ op: "and", items: [{ field: "STATUS", cmp: "eq", value: "ACSC" }] }, cliq).valid);

// --- shape checks
check("unknown comparator rejected",
    errs({ op: "and", items: [{ field: "STATUS", cmp: "regex", value: ".*" }] }, cliq)
        .some(e => e.indexOf("unknown comparator") !== -1));

check("bad op rejected",
    errs({ op: "xor", items: [{ field: "STATUS", cmp: "eq", value: "A" }] }, cliq)
        .some(e => e.indexOf("must be and or or") !== -1));

check("empty items rejected",
    errs({ op: "and", items: [] }, cliq).some(e => e.indexOf("non-empty array") !== -1));

check("in with empty list rejected",
    errs({ op: "and", items: [{ field: "STATUS", cmp: "in", value: [] }] }, cliq)
        .some(e => e.indexOf("non-empty list") !== -1));

check("in with a list accepted",
    CT.validate({ op: "and", items: [{ field: "STATUS", cmp: "in", value: ["A","B"] }] }, cliq).valid);

check("eq with a list rejected",
    errs({ op: "and", items: [{ field: "STATUS", cmp: "eq", value: ["A","B"] }] }, cliq)
        .some(e => e.indexOf("single value, not a list") !== -1));

check("eq with no value rejected",
    errs({ op: "and", items: [{ field: "STATUS", cmp: "eq" }] }, cliq)
        .some(e => e.indexOf("requires a value") !== -1));

check("isnull takes no value",
    errs({ op: "and", items: [{ field: "STATUS", cmp: "isnull", value: "x" }] }, cliq)
        .some(e => e.indexOf("takes no value") !== -1));

check("isnull without value accepted",
    CT.validate({ op: "and", items: [{ field: "STATUS", cmp: "isnull" }] }, cliq).valid);

check("hybrid group+condition node rejected",
    errs({ op: "and", field: "STATUS", items: [] }, cliq)
        .some(e => e.indexOf("not both") !== -1));

check("array at root rejected",
    errs([{ field: "STATUS", cmp: "eq", value: "A" }], cliq)
        .some(e => e.indexOf("expected an object") !== -1));

// --- depth guard
let deep = { field: "STATUS", cmp: "eq", value: "A" };
for (let i = 0; i < 12; i++) { deep = { op: "and", items: [deep] }; }
check("over-deep nesting rejected",
    errs(deep, cliq).some(e => e.indexOf("nested deeper") !== -1));

// --- parse errors
check("invalid JSON reported, not thrown",
    CT.parseAndValidate("{not json", cliq).errors.some(e => e.indexOf("not valid JSON") !== -1));

// --- describe()
const desc = CT.describe({ op: "and", items: [
    { field: "STATUS", cmp: "ne", value: "RJCT" },
    { op: "or", items: [
        { field: "DIRECTION", cmp: "eq", value: "Inward" },
        { field: "AMOUNT", cmp: "gte", value: 1000 }]}
]}, cliq);
console.log("  describe -> " + desc);
check("describe uses display labels and nests",
    desc.indexOf("Status does not equal") !== -1 && desc.indexOf(" OR ") !== -1);

console.log(`\n${pass} passed, ${fail} failed`);
process.exit(fail === 0 ? 0 : 1);
