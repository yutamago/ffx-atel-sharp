// SPDX-License-Identifier: MIT
//
// Runs inside VS Code (runTest.ts): the extension's inlay hint and hover on the made-up file, in two languages.

import * as assert from "node:assert/strict";
import * as vscode from "vscode";

async function hintsAndHover(uri: vscode.Uri): Promise<{ hints: string[]; hover: string }> {
    const doc = await vscode.workspace.openTextDocument(uri);
    await vscode.window.showTextDocument(doc);
    const range = doc.validateRange(new vscode.Range(0, 0, doc.lineCount, 0));
    // The first request may come back empty while the editor is still opening; ask again for a while.
    let hints: vscode.InlayHint[] = [];
    for (let attempt = 0; attempt < 20 && hints.length === 0; attempt++) {
        if (attempt > 0) await new Promise((r) => setTimeout(r, 250));
        hints = (await vscode.commands.executeCommand<vscode.InlayHint[]>("vscode.executeInlayHintProvider", uri, range)) ?? [];
    }
    const line = doc.getText().split("\n").findIndex((l) => l.includes("Text[1]"));
    const column = doc.lineAt(line).text.indexOf("Text[1]") + 2;
    const hovers = (await vscode.commands.executeCommand<vscode.Hover[]>("vscode.executeHoverProvider", uri, new vscode.Position(line, column))) ?? [];
    const hover = hovers
        .flatMap((h) => h.contents)
        .map((c) => (typeof c === "string" ? c : c.value))
        .join("\n");
    return { hints: hints.map((h) => (typeof h.label === "string" ? h.label : h.label.map((p) => p.value).join(""))), hover };
}

export async function run(): Promise<void> {
    const uri = vscode.Uri.file(process.env.ATEL_TEST_FILE!);
    const doc = await vscode.workspace.openTextDocument(uri);
    assert.equal(doc.languageId, "csharp");

    const en = await hintsAndHover(uri);
    console.log("en:", JSON.stringify(en));
    assert.deepEqual(en.hints, [JSON.stringify("{name:Tidus} ⏎ Made-up line.")]);
    assert.match(en.hover, /aaaa0100/);
    assert.match(en.hover, /Made-up line\./);

    await vscode.workspace.getConfiguration("atel").update("textLanguage", "de", vscode.ConfigurationTarget.Global);
    const de = await hintsAndHover(uri);
    console.log("de:", JSON.stringify(de));
    assert.deepEqual(de.hints, [JSON.stringify("{name:Tidus} ⏎ Erfundene Zeile.")]);
    assert.match(de.hover, /Erfundene Zeile\./);
}
