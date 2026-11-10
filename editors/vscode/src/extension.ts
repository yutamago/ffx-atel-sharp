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
    exportedLanguages,
    HEAD_LENGTH,
    findNamesRoot,
    findTextRoot,
    hintLabel,
    messageFull,
    messageHint,
    messageParts,
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

/** The nearest `.atel/text` and `.atel/names` by folder: looked up once per folder, not per request. */
const textRoots = new Map<string, string | undefined>();
const namesRoots = new Map<string, string | undefined>();

function nearest(roots: Map<string, string | undefined>, file: string, find: (p: string) => string | undefined): string | undefined {
    const dir = path.dirname(file);
    if (!roots.has(dir)) roots.set(dir, find(file));
    return roots.get(dir);
}

/** A document's scripts by its version: found once per change, not on every scroll. */
const scriptCache = new Map<string, { version: number; scripts: ScriptRef[] }>();

function scriptsOfDocument(document: vscode.TextDocument): ScriptRef[] {
    const key = document.uri.toString();
    const cached = scriptCache.get(key);
    if (cached && cached.version === document.version) return cached.scripts;
    // Only the file's start: getText() of a whole big worker is megabytes.
    const head = document.getText(new vscode.Range(new vscode.Position(0, 0), document.positionAt(HEAD_LENGTH)));
    const scripts = scriptsOf(head, document.uri.fsPath);
    scriptCache.set(key, { version: document.version, scripts });
    return scripts;
}

function clearCaches(): void {
    cache.clear();
    namesCache.clear();
    textRoots.clear();
    namesRoots.clear();
    scriptCache.clear();
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
        maxLength: c.get<number>("inlayHints.maxLength", 40),
    };
}

function textRoot(document: vscode.TextDocument, s: Settings): string | undefined {
    if (s.directory) {
        if (path.isAbsolute(s.directory)) return s.directory;
        const folder = vscode.workspace.getWorkspaceFolder(document.uri);
        return folder ? path.join(folder.uri.fsPath, s.directory) : undefined;
    }
    return nearest(textRoots, document.uri.fsPath, (p) => findTextRoot(p));
}

function namesRoot(document: vscode.TextDocument, s: Settings): string | undefined {
    if (s.namesDirectory) {
        if (path.isAbsolute(s.namesDirectory)) return s.namesDirectory;
        const folder = vscode.workspace.getWorkspaceFolder(document.uri);
        return folder ? path.join(folder.uri.fsPath, s.namesDirectory) : undefined;
    }
    return nearest(namesRoots, document.uri.fsPath, (p) => findNamesRoot(p));
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
    return { root: textRoot(document, s), scripts: scriptsOfDocument(document), s };
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
                const label = messageHint(first, ctx.s.maxLength) + (others > 0 ? ` (+${others})` : "");
                const hint = new vscode.InlayHint(new vscode.Position(line, use.end), label, vscode.InlayHintKind.Parameter);
                hint.paddingLeft = true;
                // The whole message (each script's, for a library class) when the hint is hovered.
                const tooltip = new vscode.MarkdownString();
                for (const f of found.slice(0, 8)) {
                    if (found.length > 1) tooltip.appendMarkdown(`**${f.script.name}**\n\n`);
                    tooltip.appendCodeblock(messageFull(f.text as string), "text");
                }
                hint.tooltip = tooltip;
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
        const root = namesRoot(document, s);
        if (root) languageLinks(md, root, s.language);
        return new vscode.Hover(md, new vscode.Range(position.line, use.start, position.line, use.end));
    }
}

/** The other exported languages as links that switch to them: `Language: en · [de](…) · [fr](…)`. */
function languageLinks(md: vscode.MarkdownString, root: string, current: string): void {
    const languages = exportedLanguages(root);
    if (languages.length < 2) return;
    const links = languages.map((l) =>
        l === current ? `**${l}**` : `[${l}](command:atel.setTextLanguage?${encodeURIComponent(JSON.stringify([l]))} "Show ${l}")`,
    );
    md.appendMarkdown(`Language: ${links.join(" · ")}\n\n`);
    md.isTrusted = { enabledCommands: ["atel.setTextLanguage"] };
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
        const languages = exportedLanguages(ctx.root);
        if (!languages.includes(ctx.s.language)) {
            const have = languages.length > 0 ? `; exported: ${languages.join(", ")}` : "";
            md.appendMarkdown(
                `Message ${use.index}: no \`${ctx.s.language}\` text (\`atel text export --lang ${ctx.s.language}\` exports it)${have}.\n\n`,
            );
            languageLinks(md, ctx.root, ctx.s.language);
            return new vscode.Hover(md, range);
        }
        for (const { script, text } of lookup(ctx, use.index).slice(0, 8)) {
            const parts = typeof text === "string" ? messageParts(text) : undefined;
            const speaker = parts?.speaker ? `: ${parts.speaker}` : "";
            md.appendMarkdown(`**${script.name}**, message ${use.index} (${ctx.s.language})${speaker}\n\n`);
            if (parts) md.appendCodeblock(parts.lines.join("\n"), "text");
            else if (script.category === "monster")
                md.appendMarkdown("_A monster has no message table: the index is into the current battle's._\n\n");
            else if (text === null) md.appendMarkdown("_No text in this message._\n\n");
            else md.appendMarkdown(`_No message ${use.index} for ${script.name} in \`${ctx.s.language}\`._\n\n`);
        }
        languageLinks(md, ctx.root, ctx.s.language);
        return new vscode.Hover(md, range);
    }
}

/** Where the language setting is saved: where it is set already, else the user's settings. */
async function setLanguage(language: string): Promise<void> {
    const c = vscode.workspace.getConfiguration("atel");
    const where = c.inspect<string>("textLanguage");
    const target =
        where?.workspaceFolderValue !== undefined
            ? vscode.ConfigurationTarget.WorkspaceFolder
            : where?.workspaceValue !== undefined
              ? vscode.ConfigurationTarget.Workspace
              : vscode.ConfigurationTarget.Global;
    await c.update("textLanguage", language, target);
}

/** A pick of the languages the active file's text folder has (all eight when there is none). */
async function selectLanguage(): Promise<void> {
    const document = vscode.window.activeTextEditor?.document;
    const s = document ? settings(document.uri) : undefined;
    const root = document && s ? textRoot(document, s) : undefined;
    const exported = root ? exportedLanguages(root) : [];
    const all = ["en", "ja", "fr", "es", "de", "it", "ko", "zh"];
    const items = (exported.length > 0 ? exported : all).map((l) => ({
        label: l,
        description: l === (s?.language ?? "en") ? "current" : undefined,
    }));
    const picked = await vscode.window.showQuickPick(items, { placeHolder: "The language of the message text and names" });
    if (picked) await setLanguage(picked.label);
}

export function activate(extension: vscode.ExtensionContext): void {
    const selector: vscode.DocumentSelector = { language: "csharp", scheme: "file" };
    const hints = new TextHints();
    const refresh = () => {
        clearCaches();
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
        vscode.workspace.onDidCloseTextDocument((d) => scriptCache.delete(d.uri.toString())),
        vscode.commands.registerCommand("atel.selectTextLanguage", selectLanguage),
        vscode.commands.registerCommand("atel.setTextLanguage", (language: unknown) =>
            typeof language === "string" ? setLanguage(language) : selectLanguage(),
        ),
        hints.changed,
    );
}

export function deactivate(): void {
    clearCaches();
}
