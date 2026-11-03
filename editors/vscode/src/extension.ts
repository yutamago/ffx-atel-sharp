// SPDX-License-Identifier: MIT
//
// Atel# message text: an inlay hint after each Text[n] of a C# file in an Atel source tree, and a hover, with
// the message from the player's own tables (`atel text export`, docs/editor.md); the same for the typed values
// that index the game's name tables (`Target.Biran`, `Command.Attack`, `Map.Hiku0800`, `Battle.Kami00_00`;
// `atel names export`). No language server: it reads the file's text and complements the C# extension.

import * as fs from "node:fs";
import * as path from "node:path";
import * as vscode from "vscode";
import {
    findNamesRoot,
    findTextRoot,
    hintLabel,
    lookupName,
    NameEntry,
    namesFilePath,
    NamesFile,
    NameUse,
    nameUses,
    parseNamesFile,
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

/** Parsed names files by path, as `cache`. */
const namesCache = new Map<string, NamesFile | undefined>();

function loadNamesFile(file: string): NamesFile | undefined {
    if (namesCache.has(file)) return namesCache.get(file);
    let parsed: NamesFile | undefined;
    try {
        parsed = parseNamesFile(fs.readFileSync(file, "utf8"));
    } catch {
        parsed = undefined;
    }
    namesCache.set(file, parsed);
    return parsed;
}

interface Settings {
    language: string;
    directory: string;
    namesDirectory: string;
    hints: boolean;
    nameHints: boolean;
    maxLength: number;
}

function settings(scope: vscode.Uri): Settings {
    const c = vscode.workspace.getConfiguration("atel", scope);
    return {
        language: c.get<string>("textLanguage", "en"),
        directory: c.get<string>("textDirectory", ""),
        namesDirectory: c.get<string>("namesDirectory", ""),
        hints: c.get<boolean>("inlayHints.enabled", true),
        nameHints: c.get<boolean>("inlayHints.names", true),
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

function namesRoot(document: vscode.TextDocument, s: Settings): string | undefined {
    if (s.namesDirectory) {
        if (path.isAbsolute(s.namesDirectory)) return s.namesDirectory;
        const folder = vscode.workspace.getWorkspaceFolder(document.uri);
        return folder ? path.join(folder.uri.fsPath, s.namesDirectory) : undefined;
    }
    return findNamesRoot(document.uri.fsPath);
}

/** The game's name for a typed value, from `<names root>/<language>/<table>.json`. */
function lookupNameUse(root: string | undefined, language: string, use: NameUse): NameEntry | undefined {
    const file = root ? namesFilePath(root, language, use.type) : undefined;
    return file ? lookupName(loadNamesFile(file), use) : undefined;
}

function hex(id: number): string {
    return "0x" + id.toString(16);
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
        if (!ctx.s.hints) return [];
        const hints: vscode.InlayHint[] = [];
        const names = ctx.s.nameHints ? namesRoot(document, ctx.s) : undefined;
        const texts = ctx.root !== undefined && ctx.scripts.length > 0;
        for (let line = range.start.line; line <= range.end.line && line < document.lineCount; line++) {
            const lineText = document.lineAt(line).text;
            if (names) {
                for (const use of nameUses(lineText)) {
                    const entry = lookupNameUse(names, ctx.s.language, use);
                    if (!entry) continue;
                    const hint = new vscode.InlayHint(
                        new vscode.Position(line, use.end),
                        JSON.stringify(hintLabel(entry.name, ctx.s.maxLength)),
                        vscode.InlayHintKind.Parameter,
                    );
                    hint.paddingLeft = true;
                    hints.push(hint);
                }
            }
            if (!texts) continue;
            for (const use of textUses(lineText)) {
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

class NameHover implements vscode.HoverProvider {
    provideHover(document: vscode.TextDocument, position: vscode.Position): vscode.Hover | undefined {
        const use = nameUses(document.lineAt(position.line).text).find(
            (u) => position.character >= u.start && position.character <= u.end,
        );
        if (!use) return undefined;
        const s = settings(document.uri);
        const entry = lookupNameUse(namesRoot(document, s), s.language, use);
        if (!entry) return undefined;
        const md = new vscode.MarkdownString();
        const what = entry.member ? `${use.type}.${entry.member}` : `(${use.type})${hex(entry.id)}`;
        md.appendMarkdown(`**${what}** (${hex(entry.id)}, ${s.language})\n\n`);
        md.appendCodeblock(entry.name, "text");
        return new vscode.Hover(md, new vscode.Range(position.line, use.start, position.line, use.end));
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
        namesCache.clear();
        hints.changed.fire();
    };
    const watcher = vscode.workspace.createFileSystemWatcher("**/.atel/{text,names}/**/*.json");
    extension.subscriptions.push(
        vscode.languages.registerInlayHintsProvider(selector, hints),
        vscode.languages.registerHoverProvider(selector, new TextHover()),
        vscode.languages.registerHoverProvider(selector, new NameHover()),
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
    namesCache.clear();
}
