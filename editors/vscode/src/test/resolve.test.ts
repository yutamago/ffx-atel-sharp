// SPDX-License-Identifier: MIT
//
// The parts of the extension that need no VS Code: `npm test` (node --test). Made-up names and text only.

import * as assert from "node:assert/strict";
import * as path from "node:path";
import { test } from "node:test";
import {
    exportedLanguages,
    findNamesRoot,
    findTextRoot,
    HEAD_LENGTH,
    hintLabel,
    messageFull,
    messageHint,
    messageParts,
    lookupName,
    namesFilePath,
    nameUses,
    parseNamesFile,
    parseTextFile,
    scriptsOf,
    textFilePath,
    textUses,
} from "../resolve";

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
    assert.equal(hintLabel("One\nTwo", 60), "One Two");
    assert.equal(hintLabel("abcdefghij", 5), "abcd…");
    // Cut at the last space in the second half, else in the word.
    assert.equal(hintLabel("A made-up line that is long", 20), "A made-up line that…");
    assert.equal(hintLabel("Abcdefghijklmnop qrstuvwxyz", 20), "Abcdefghijklmnop…");
});

test("a message's speaker and lines", () => {
    assert.deepEqual(messageParts("{name:Tidus}\nMade-up line.\nAnd another."), { speaker: "Tidus", lines: ["Made-up line.", "And another."] });
    assert.deepEqual(messageParts("{wait:0}{name:Wakka}\nMade-up."), { speaker: "Wakka", lines: ["Made-up."] });
    // A first line left empty: a speaker the window does not name.
    assert.deepEqual(messageParts("\nMade-up question?"), { lines: ["Made-up question?"] });
    // Anything else on the first line is text: a plain name, a choice, codes.
    assert.deepEqual(messageParts("Someone\nMade-up."), { lines: ["Someone", "Made-up."] });
    assert.deepEqual(messageParts("{wait:0}\nMade-up."), { lines: ["{wait:0}", "Made-up."] });
    assert.deepEqual(messageParts("{name:Tidus}"), { lines: ["{name:Tidus}"] });
    assert.deepEqual(messageParts("{name:Tidus} and {name:Yuna}\nMade-up."), { lines: ["{name:Tidus} and {name:Yuna}", "Made-up."] });

    assert.equal(messageHint("{name:Tidus}\nMade-up line.\nAnd \"another\".", 60), 'Tidus: "Made-up line. And \\"another\\"."');
    assert.equal(messageHint("\nMade-up question?", 60), '"Made-up question?"');
    assert.equal(messageHint("First.", 60), '"First."');
    assert.equal(messageFull("{name:Tidus}\nMade-up line.\nAnd another."), "Tidus:\nMade-up line.\nAnd another.");
    assert.equal(messageFull("\nMade-up question?"), "Made-up question?");
});

test("lists the exported languages", () => {
    assert.deepEqual(exportedLanguages("/r", () => ["de", "en"]), ["de", "en"]);
    assert.deepEqual(
        exportedLanguages("/r", () => {
            throw new Error("missing");
        }),
        [],
    );
});

test("reads only the start of a big file for its script", () => {
    const filler = "// " + "x".repeat(100) + "\n";
    const big = "namespace Events.Aa.Aaaa0100;\n" + filler.repeat(Math.ceil((HEAD_LENGTH * 4) / filler.length)) + '[Event("zzzz9999")]\n';
    assert.deepEqual(scriptsOf(big, "/x.cs"), [{ name: "aaaa0100", category: "event" }]);
});

test("finds typed values with a name table outside comments", () => {
    assert.deepEqual(nameUses("        Btl.UseCommand(Target.Aaaa, Command.Bbbb);"), [
        { type: "Target", member: "Aaaa", start: 23, end: 34 },
        { type: "Command", member: "Bbbb", start: 36, end: 48 },
    ]);
    assert.deepEqual(nameUses("x((Command)0x3001, (Ffx.Target)4097, Ffx.Map.Cccc0100);").map((u) => [u.type, u.id ?? u.member]), [
        ["Command", 0x3001],
        ["Target", 4097],
        ["Map", "Cccc0100"],
    ]);
    assert.deepEqual(nameUses("if (Btl.GetBattleId() == Battle.Dddd00_00) // Map.Eeee0100"), [
        { type: "Battle", member: "Dddd00_00", start: 25, end: 41 },
    ]);
    assert.deepEqual(nameUses("// Target.Aaaa"), []);
    assert.deepEqual(nameUses("x.Target.Aaaa; (Model)0x3000"), []);
    assert.deepEqual(nameUses("namespace Battles.Zz.Zz00_00; MyMap.X; ActorProperty.Hp; [Battle(\"zz00_00\")]"), []);
});

test("reads a names file and looks up members and ids", () => {
    const json = JSON.stringify({
        table: "commands",
        type: "Command",
        language: "en",
        names: [
            { id: 0x3000, member: "Bbbb", name: "Made-up command" },
            { id: 0xa000, name: "A made-up key item" },
        ],
    });
    const file = parseNamesFile(json);
    assert.equal(lookupName(file, { type: "Command", member: "Bbbb", start: 0, end: 0 })?.name, "Made-up command");
    assert.equal(lookupName(file, { type: "Command", id: 0xa000, start: 0, end: 0 })?.name, "A made-up key item");
    assert.equal(lookupName(file, { type: "Command", member: "Zzzz", start: 0, end: 0 }), undefined);
    assert.equal(parseNamesFile("{}"), undefined);
    assert.equal(namesFilePath("/r", "de", "Target"), path.join("/r", "de", "monsters.json"));
    assert.equal(namesFilePath("/r", "de", "Model"), undefined);
});

test("finds the nearest .atel/names", () => {
    const root = path.join("/src", ".atel", "names");
    assert.equal(findNamesRoot(path.join("/src", "Events", "Aa", "W01.cs"), (p) => p === root), root);
    assert.equal(findNamesRoot(path.join("/src", "W01.cs"), () => false), undefined);
});
