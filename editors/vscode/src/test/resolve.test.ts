// SPDX-License-Identifier: MIT
//
// The parts of the extension that need no VS Code: `npm test` (node --test). Made-up names and text only.

import * as assert from "node:assert/strict";
import * as path from "node:path";
import { test } from "node:test";
import { findTextRoot, hintLabel, parseTextFile, scriptsOf, textFilePath, textUses } from "../resolve";

test("finds Text[n] uses outside comments", () => {
    assert.deepEqual(textUses("        Common.MsgSetText(0, Text[12]);"), [{ index: 12, start: 29, end: 37 }]);
    assert.deepEqual(
        textUses("x(Text[1], Text [ 2 ]);").map((u) => u.index),
        [1, 2],
    );
    assert.deepEqual(textUses("// Text[3]"), []);
    assert.deepEqual(textUses("y(Text[4]); // Text[5]").map((u) => u.index), [4]);
    assert.deepEqual(textUses("MyText[6]; Text[i]"), []);
});

test("resolves the script from the decompiler's first line", () => {
    const text = "// event/obj/aa/aaaa0100/aaaa0100.ebp, decompiled by atel.\nusing Ffx;\nnamespace Events.Aa.Other;\n";
    assert.deepEqual(scriptsOf(text, "/src/Events/Aa/X/W01.cs"), [{ name: "aaaa0100", category: "event" }]);
    const battle = "// battle/btl/zz00_00/zz00_00.bin, decompiled by atel.\n";
    assert.deepEqual(scriptsOf(battle, "/x.cs"), [{ name: "zz00_00", category: "battle" }]);
    const monster = "// battle/mon/_m999/m999.bin, decompiled by atel.\n";
    assert.deepEqual(scriptsOf(monster, "/x.cs"), [{ name: "m999", category: "monster" }]);
});

test("resolves the script from its attribute, the namespace or the folder", () => {
    assert.deepEqual(scriptsOf('[Event("aaaa0100")]\npublic sealed partial class Aaaa0100 : Script { }', "/x.cs"), [
        { name: "aaaa0100", category: "event" },
    ]);
    assert.deepEqual(scriptsOf("namespace Battles.Zz.Zz00_00;\nclass W03 { }", "/x.cs"), [
        { name: "zz00_00", category: "battle" },
    ]);
    assert.deepEqual(scriptsOf("namespace Mod;\nclass A { }", path.join("/src", "Aaaa0100", "A.cs")), [
        { name: "aaaa0100", category: "unknown" },
    ]);
});

test("a library class reads the tables of its users", () => {
    const text = [
        "// The code of 3 workers, decompiled by atel:",
        "//   Events.Aa.Aaaa0100.W03",
        "//   Events.Aa.Aaaa0100.W07",
        "//   Battles.Zz.Zz00_00.W01",
        "using Ffx;",
        "namespace Library;",
    ].join("\n");
    assert.deepEqual(scriptsOf(text, "/src/Library/Shared0001.cs"), [
        { name: "aaaa0100", category: "event" },
        { name: "zz00_00", category: "battle" },
    ]);
});

test("finds the nearest .atel/text", () => {
    const root = path.join(path.sep, "src");
    const have = new Set([path.join(root, ".atel", "text")]);
    assert.equal(findTextRoot(path.join(root, "Events", "Aa", "Aaaa0100", "W01.cs"), (p) => have.has(p)), path.join(root, ".atel", "text"));
    assert.equal(findTextRoot(path.join(path.sep, "elsewhere", "a.cs"), (p) => have.has(p)), undefined);
    assert.equal(textFilePath(path.join(root, ".atel", "text"), "de", "aaaa0100"), path.join(root, ".atel", "text", "de", "aaaa0100.json"));
});

test("reads text files and shortens hints", () => {
    const file = parseTextFile('{"script":"aaaa0100","category":"event","language":"en","messages":["{name:Tidus}\\nHello!",null]}');
    assert.equal(file?.messages[0], "{name:Tidus}\nHello!");
    assert.equal(file?.messages[1], null);
    assert.equal(parseTextFile("not json"), undefined);
    assert.equal(hintLabel("One\nTwo", 60), "One ⏎ Two");
    assert.equal(hintLabel("abcdefghij", 5), "abcd…");
});
