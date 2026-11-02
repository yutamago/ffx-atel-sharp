// SPDX-License-Identifier: MIT
//
// Atel# message text: an inlay hint after each Text[n] of a C# file in an Atel source tree, and a hover, with
// the message from the player's own tables (`atel text export`, docs/editor.md). No language server: it reads
// the file's text and complements the C# extension.

import * as fs from "node:fs";
import * as path from "node:path";
import * as vscode from "vscode";
import {
    findTextRoot,
    hintLabel,
    parseTextFile,
    ScriptRef,
    scriptsOf,
    TextFile,
    textFilePath,
    textUses,
} from "./resolve";

/** Parsed text files by path; undefined when the file is missing or unreadable. */
const cache = new Map<string, TextFile | undefined>();

function loadTextFile(file: string): TextFile | undefined {
    if (cache.has(file)) return cache.get(file);
    let parsed: TextFile | undefined;
    try {
        parsed = parseTextFile(fs.readFileSync(file, "utf8"));
    } catch {
        parsed = undefined;
    }
    cache.set(file, parsed);
    return parsed;
}

interface Settings {
    language: string;
    directory: string;
    hints: boolean;
    maxLength: number;
}

function settings(scope: vscode.Uri): Settings {
    const c = vscode.workspace.getConfiguration("atel", scope);
    return {
        language: c.get<string>("textLanguage", "en"),
        directory: c.get<string>("textDirectory", ""),
        hints: c.get<boolean>("inlayHints.enabled", true),
        maxLength: c.get<number>("inlayHints.maxLength", 60),
    };
}

function textRoot(document: vscode.TextDocument, s: Settings): string | undefined {
    if (s.directory) {
        if (path.isAbsolute(s.directory)) return s.directory;
        const folder = vscode.workspace.getWorkspaceFolder(document.uri);
        return folder ? path.join(folder.uri.fsPath, s.directory) : undefined;
    }
    return findTextRoot(document.uri.fsPath);
}

interface Context {
    root: string | undefined;
    scripts: ScriptRef[];
    s: Settings;
}

function context(document: vscode.TextDocument): Context {
    const s = settings(document.uri);
    return { root: textRoot(document, s), scripts: scriptsOf(document.getText(), document.uri.fsPath), s };
}

/** The message of each script the file may belong to (one, or several for a library class). */
function lookup(ctx: Context, index: number): { script: ScriptRef; text: string | null | undefined }[] {
    return ctx.scripts.map((script) => {
        const file = ctx.root ? loadTextFile(textFilePath(ctx.root, ctx.s.language, script.name)) : undefined;
        return { script, text: file ? file.messages[index] : undefined };
    });
}

class TextHints implements vscode.InlayHintsProvider {
    readonly changed = new vscode.EventEmitter<void>();
    readonly onDidChangeInlayHints = this.changed.event;

    provideInlayHints(document: vscode.TextDocument, range: vscode.Range): vscode.InlayHint[] {
        const ctx = context(document);
        if (!ctx.s.hints || !ctx.root || ctx.scripts.length === 0) return [];
        const hints: vscode.InlayHint[] = [];
        for (let line = range.start.line; line <= range.end.line && line < document.lineCount; line++) {
            for (const use of textUses(document.lineAt(line).text)) {
                const found = lookup(ctx, use.index).filter((f) => typeof f.text === "string");
                if (found.length === 0) continue;
                const first = found[0].text as string;
                const others = new Set(found.map((f) => f.text)).size - 1;
                const label = JSON.stringify(hintLabel(first, ctx.s.maxLength)) + (others > 0 ? ` (+${others})` : "");
                const hint = new vscode.InlayHint(new vscode.Position(line, use.end), label, vscode.InlayHintKind.Parameter);
                hint.paddingLeft = true;
                hints.push(hint);
            }
        }
        return hints;
    }
}

class TextHover implements vscode.HoverProvider {
    provideHover(document: vscode.TextDocument, position: vscode.Position): vscode.Hover | undefined {
        const use = textUses(document.lineAt(position.line).text).find(
            (u) => position.character >= u.start && position.character <= u.end,
        );
        if (!use) return undefined;
        const ctx = context(document);
        const md = new vscode.MarkdownString();
        const range = new vscode.Range(position.line, use.start, position.line, use.end);
        if (ctx.scripts.length === 0) return undefined;
        if (!ctx.root) {
            md.appendMarkdown(
                `Message ${use.index}: no message text found. Run \`atel text export -o <source dir>\` (it writes \`.atel/text\`), or set \`atel.textDirectory\`.`,
            );
            return new vscode.Hover(md, range);
        }
        for (const { script, text } of lookup(ctx, use.index).slice(0, 8)) {
            md.appendMarkdown(`**${script.name}**, message ${use.index} (${ctx.s.language})\n\n`);
            if (typeof text === "string") md.appendCodeblock(text, "text");
            else if (script.category === "monster")
                md.appendMarkdown("_A monster has no message table: the index is into the current battle's._\n\n");
            else if (text === null) md.appendMarkdown("_No text in this message._\n\n");
            else md.appendMarkdown(`_No message ${use.index} for ${script.name} in \`${ctx.s.language}\`._\n\n`);
        }
        return new vscode.Hover(md, range);
    }
}

export function activate(extension: vscode.ExtensionContext): void {
    const selector: vscode.DocumentSelector = { language: "csharp", scheme: "file" };
    const hints = new TextHints();
    const refresh = () => {
        cache.clear();
        hints.changed.fire();
    };
    const watcher = vscode.workspace.createFileSystemWatcher("**/.atel/text/**/*.json");
    extension.subscriptions.push(
        vscode.languages.registerInlayHintsProvider(selector, hints),
        vscode.languages.registerHoverProvider(selector, new TextHover()),
        watcher,
        watcher.onDidChange(refresh),
        watcher.onDidCreate(refresh),
        watcher.onDidDelete(refresh),
        vscode.workspace.onDidChangeConfiguration((e) => {
            if (e.affectsConfiguration("atel")) refresh();
        }),
        hints.changed,
    );
}

export function deactivate(): void {
    cache.clear();
}
