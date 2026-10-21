import test from "node:test";
import assert from "node:assert/strict";
import {
  archiveName,
  nameError,
  destinationError,
  extractionTargets,
} from "../src/workflow.js";

test("switching format replaces an existing archive suffix without duplicating it", () => {
  assert.equal(archiveName(" Report.ZIP ", "7z"), "Report.7z");
  assert.equal(archiveName("报告.tar.gz", "zip"), "报告.tar.gz.zip");
});
test("invalid or reserved Windows names are explained before submission", () => {
  for (const value of [
    "",
    " ",
    ".zip",
    "CON.zip",
    "nul.txt",
    "com1",
    "folder/file",
    "a:b",
    "report.",
  ])
    assert.notEqual(nameError(value), "", value);
  for (const value of ["报告", "project-kit", "my report.zip"])
    assert.equal(nameError(value), "", value);
});
test("destination requires an absolute drive path or share", () => {
  for (const value of [
    "D:\\Downloads\\Project",
    "C:/Exports",
    "\\\\server\\share\\folder",
  ])
    assert.equal(destinationError(value), "", value);
  for (const value of ["", "relative", "D:folder", "\\\\server"])
    assert.notEqual(destinationError(value), "", value);
});
test("multi-selection preview preserves each path and reacts to structure toggle", () => {
  const selected = [
    { parent: "docs/", name: "README.md" },
    { parent: "config/", name: "config.yml" },
  ];
  assert.deepEqual(extractionTargets(selected, "D:\\Output\\", true), [
    "D:\\Output\\docs\\README.md",
    "D:\\Output\\config\\config.yml",
  ]);
  assert.deepEqual(extractionTargets(selected, "D:\\Output", false), [
    "D:\\Output\\README.md",
    "D:\\Output\\config.yml",
  ]);
});
