// SPDX-License-Identifier: MIT
//
// Runs inside VS Code (runTest.ts): the extension's inlay hints and hovers on the made-up file (a message and two
// names), in two languages.

import * as assert from "node:assert/strict";
import * as vscode from "vscode";

async function hoverAt(uri: vscode.Uri, doc: vscode.TextDocument, needle: string): Promise<string> {
    const line = doc.getText().split("\n").findIndex((l) => l.includes(needle));
    const column = doc.lineAt(line).text.indexOf(needle) + 2;
    const hovers = (await vscode.commands.executeCommand<vscode.Hover[]>("vscode.executeHoverProvider", uri, new vscode.Position(line, column))) ?? [];
    return hovers
        .flatMap((h) => h.contents)
        .map((c) => (typeof c === "string" ? c : c.value))
        .join("\n");
}

async function hintsAndHover(uri: vscode.Uri): Promise<{ hints: string[]; hover: string; nameHover: string }> {
    const doc = await vscode.workspace.openTextDocument(uri);
    await vscode.window.showTextDocument(doc);
    const range = doc.validateRange(new vscode.Range(0, 0, doc.lineCount, 0));
    // The first request may come back empty while the editor is still opening; ask again for a while.
    let hints: vscode.InlayHint[] = [];
    for (let attempt = 0; attempt < 20 && hints.length === 0; attempt++) {
        if (attempt > 0) await new Promise((r) => setTimeout(r, 250));
        hints = (await vscode.commands.executeCommand<vscode.InlayHint[]>("vscode.executeInlayHintProvider", uri, range)) ?? [];
    }
    const hover = await hoverAt(uri, doc, "Text[1]");
    const nameHover = await hoverAt(uri, doc, "Target.Aaaa");
    return { hints: hints.map((h) => (typeof h.label === "string" ? h.label : h.label.map((p) => p.value).join(""))), hover, nameHover };
}

export async function run(): Promise<void> {
    const uri = vscode.Uri.file(process.env.ATEL_TEST_FILE!);
    const doc = await vscode.workspace.openTextDocument(uri);
    assert.equal(doc.languageId, "csharp");

    const en = await hintsAndHover(uri);
    console.log("en:", JSON.stringify(en));
    assert.deepEqual(en.hints, [
        `Tidus: ${JSON.stringify("Made-up line.")}`,
        JSON.stringify("Made-up monster"),
        JSON.stringify("Made-up command"),
    ]);
    assert.match(en.hover, /aaaa0100/);
    assert.match(en.hover, /Made-up line\./);
    assert.match(en.hover, /\(en\): Tidus/);
    // The other exported language as a link that switches to it.
    assert.match(en.hover, /\[de\]\(command:atel\.setTextLanguage/);
    assert.match(en.nameHover, /Target\.Aaaa/);
    assert.match(en.nameHover, /Made-up monster/);

    // As the hover's link does.
    await vscode.commands.executeCommand("atel.setTextLanguage", "de");
    const de = await hintsAndHover(uri);
    console.log("de:", JSON.stringify(de));
    assert.deepEqual(de.hints, [
        `Tidus: ${JSON.stringify("Erfundene Zeile.")}`,
        JSON.stringify("Erfundenes Monster"),
        JSON.stringify("Made-up command"),
    ]);
    assert.match(de.hover, /Erfundene Zeile\./);
    assert.match(de.nameHover, /Erfundenes Monster/);
}
