// SPDX-License-Identifier: MIT
//
// What a source file's Text[n] refers to, without VS Code: which script's message table (from the file's
// text and path) and where the exported tables are. Kept free of the `vscode` module so `node --test` can
// check it.

import * as fs from "node:fs";
import * as path from "node:path";

/** A use of the message indexer: `Text[12]` at [start, end) of its line. */
export interface TextUse {
    index: number;
    start: number;
    end: number;
}

const USE = /\bText\s*\[\s*(\d+)\s*\]/g;

/** The `Text[n]` with a constant index on a line, outside a `//` comment that starts before them. */
export function textUses(line: string): TextUse[] {
    const comment = line.indexOf("//");
    const uses: TextUse[] = [];
    for (const m of line.matchAll(USE)) {
        const start = m.index ?? 0;
        if (comment >= 0 && comment < start) break;
        uses.push({ index: Number(m[1]), start, end: start + m[0].length });
    }
    return uses;
}

/** What kind of script a file belongs to; monsters have no message table of their own. */
export type Category = "event" | "battle" | "monster" | "unknown";

export interface ScriptRef {
    name: string;
    category: Category;
}

function category(rootNamespace: string): Category {
    switch (rootNamespace) {
        case "Events":
            return "event";
        case "Battles":
            return "battle";
        case "Monsters":
            return "monster";
        default:
            return "unknown";
    }
}

/** A script's name from its namespace (`Events.Bj.Bjyt0400` → `bjyt0400`). */
function fromNamespace(ns: string): ScriptRef | undefined {
    const parts = ns.split(".");
    const cat = category(parts[0]);
    if (cat === "unknown" || parts.length < 2) return undefined;
    return { name: parts[parts.length - 1].toLowerCase(), category: cat };
}

/** How much of a file's start names its script (both editors read only this much). */
export const HEAD_LENGTH = 65536;

/**
 * The scripts whose message table a file's `Text[n]` reads, most certain first (all from the file's first
 * `HEAD_LENGTH` characters):
 *  1. the first line `atel decompile` writes: `// event/obj/bj/bjyt0400/bjyt0400.ebp, decompiled by atel.`;
 *  2. the script attribute: `[Event("bjyt0400")]`, `[Battle("…")]`, `[Monster("…")]`;
 *  3. a library class (`Library/Shared0001.cs`), shared by workers of several scripts: each listed user
 *     (`//   Events.Bj.Bjyt0400.W03`);
 *  4. the namespace: `namespace Events.Bj.Bjyt0400;`;
 *  5. the folder's name.
 */
export function scriptsOf(text: string, filePath: string): ScriptRef[] {
    // Only the file's start: a decompiled worker can be megabytes (bltz0000's W36), and what names the script is
    // always at the top.
    const head = text.length > HEAD_LENGTH ? text.slice(0, HEAD_LENGTH) : text;
    const decompiled = /^\/\/ ((?:event|battle)\/[^\s,]+), decompiled by atel\./.exec(head);
    if (decompiled) {
        const p = decompiled[1];
        const name = path.posix.basename(p).replace(/\.[^.]*$/, "").toLowerCase();
        const cat: Category = p.startsWith("event/") ? "event" : p.startsWith("battle/mon/") ? "monster" : "battle";
        return [{ name, category: cat }];
    }
    const attribute = /\[\s*(?:Ffx\.Atel\.)?(Event|Battle|Monster)(?:Attribute)?\s*\(\s*"([^"]+)"\s*\)/.exec(head);
    if (attribute) {
        const cat = attribute[1].toLowerCase() as Category;
        return [{ name: attribute[2].toLowerCase(), category: cat }];
    }
    if (/^\/\/ The code of \d+ workers, decompiled by atel:/.test(head)) {
        const users: ScriptRef[] = [];
        for (const m of head.matchAll(/^\/\/ {3}([A-Za-z_][\w.]*)$/gm)) {
            const parts = m[1].split(".");
            const ref = fromNamespace(parts.slice(0, -1).join("."));
            if (ref && !users.some((u) => u.name === ref.name)) users.push(ref);
        }
        return users;
    }
    const ns = /^\s*namespace\s+([\w.]+)/m.exec(head);
    const fromNs = ns ? fromNamespace(ns[1]) : undefined;
    if (fromNs) return [fromNs];
    const folder = path.basename(path.dirname(filePath));
    return folder ? [{ name: folder.toLowerCase(), category: "unknown" }] : [];
}

/** The nearest `.atel/text` folder at or above a file's folder; undefined when there is none. */
export function findTextRoot(filePath: string, exists: (p: string) => boolean = fs.existsSync): string | undefined {
    let dir = path.dirname(filePath);
    for (;;) {
        const candidate = path.join(dir, ".atel", "text");
        if (exists(candidate)) return candidate;
        const parent = path.dirname(dir);
        if (parent === dir) return undefined;
        dir = parent;
    }
}

/** A file `atel text export` writes: `<root>/<lang>/<script>.json`. */
export interface TextFile {
    script: string;
    category: string;
    language: string;
    messages: (string | null)[];
}

export function textFilePath(root: string, language: string, script: string): string {
    return path.join(root, language, `${script}.json`);
}

export function parseTextFile(json: string): TextFile | undefined {
    try {
        const data = JSON.parse(json) as Partial<TextFile>;
        return Array.isArray(data.messages) ? (data as TextFile) : undefined;
    } catch {
        return undefined;
    }
}

/**
 * A message split for showing: who speaks it, when its first line says so, and its lines. A window's first line is
 * the speaker when it is a character's name code alone (`{name:Tidus}`, after any `{wait:n}`); a first line left
 * empty is a speaker the window does not name. Other first lines (plain names, choices) stay part of the text.
 */
export interface MessageParts {
    speaker?: string;
    lines: string[];
}

export function messageParts(text: string): MessageParts {
    const lines = text.split(/\r?\n/);
    if (lines.length < 2) return { lines };
    const first = lines[0].replace(/^(?:\s|\{wait:\d+\})+/, "");
    const name = /^\{name:([^{}]+)\}\s*$/.exec(first);
    if (name) return { speaker: name[1], lines: lines.slice(1) };
    if (first.trim() === "" && !/\{/.test(lines[0])) return { lines: lines.slice(1) };
    return { lines };
}

/** Text cut to `max` characters with an ellipsis; a word the cut would split goes, when a space is in the second half. */
export function shorten(text: string, max: number): string {
    const chars = Array.from(text);
    if (chars.length <= max) return text;
    const keep = Math.max(1, max - 1);
    let cut = chars.slice(0, keep).join("");
    // Inside a word: back to the space before it.
    const space = cut.lastIndexOf(" ");
    if (chars[keep] !== " " && space >= max / 2) cut = cut.slice(0, space);
    return cut.trimEnd() + "…";
}

/** A message on one line for an inlay hint: its lines joined by spaces (the window's breaks are only layout), cut to `max`. */
export function hintLabel(text: string, max: number): string {
    return shorten(
        text
            .split(/\r?\n/)
            .map((l) => l.trim())
            .filter((l) => l !== "")
            .join(" "),
        max,
    );
}

/** The inlay hint of a message: `Tidus: "Hey, wait! …"`, or only the quoted text without a speaker. */
export function messageHint(text: string, max: number): string {
    const parts = messageParts(text);
    const quoted = JSON.stringify(hintLabel(parts.lines.join("\n"), max));
    return parts.speaker ? `${parts.speaker}: ${quoted}` : quoted;
}

/** The whole message for a tooltip: the speaker on a line of its own, then the message's lines. */
export function messageFull(text: string): string {
    const parts = messageParts(text);
    return (parts.speaker ? `${parts.speaker}:\n` : "") + parts.lines.join("\n");
}

/** The languages a text (or names) folder has an export for: its subfolders, sorted. */
export function exportedLanguages(root: string, read: (p: string) => string[] = readDirs): string[] {
    try {
        return read(root).sort();
    } catch {
        return [];
    }
}

function readDirs(p: string): string[] {
    return fs
        .readdirSync(p, { withFileTypes: true })
        .filter((d) => d.isDirectory())
        .map((d) => d.name);
}

// Names (`atel names export`, docs/editor.md "Names"): a typed constant whose value indexes one of the game's name
// tables, `Target.Biran`, `Command.Attack`, `Map.Hiku0800`, `Battle.Kami00_00`, or a cast `(Command)0x3000`.

/** The table each type's values index: `<root>/<lang>/<table>.json`. */
export const NAME_TABLES: Readonly<Record<string, string>> = {
    Target: "monsters",
    Command: "commands",
    Map: "maps",
    Battle: "battles",
};

/** A typed value on a line: `Command.Attack` (member) or `(Command)0x3000` (id) at [start, end). */
export interface NameUse {
    type: string;
    member?: string;
    id?: number;
    start: number;
    end: number;
}

const TYPES = Object.keys(NAME_TABLES).join("|");
const NAME_USE = new RegExp(
    `\\((?:Ffx\\.)?(${TYPES})\\)\\s*(0[xX][0-9a-fA-F]+|\\d+)\\b|(?<![\\w.])(?:Ffx\\.)?(${TYPES})\\s*\\.\\s*([A-Za-z_]\\w*)`,
    "g",
);

/** The typed values with a name table on a line, outside a `//` comment that starts before them. */
export function nameUses(line: string): NameUse[] {
    const comment = line.indexOf("//");
    const uses: NameUse[] = [];
    for (const m of line.matchAll(NAME_USE)) {
        const start = m.index ?? 0;
        if (comment >= 0 && comment < start) break;
        const end = start + m[0].length;
        if (m[1]) uses.push({ type: m[1], id: Number(m[2]), start, end });
        else uses.push({ type: m[3], member: m[4], start, end });
    }
    return uses;
}

/** One entry of a names file: the value the source uses, its member if the type has one, the game's name. */
export interface NameEntry {
    id: number;
    member?: string;
    name: string;
}

/** A file `atel names export` writes, indexed. */
export interface NamesFile {
    table: string;
    type: string;
    language: string;
    byId: Map<number, NameEntry>;
    byMember: Map<string, NameEntry>;
}

export function parseNamesFile(json: string): NamesFile | undefined {
    try {
        const data = JSON.parse(json) as { table?: string; type?: string; language?: string; names?: NameEntry[] };
        if (!Array.isArray(data.names)) return undefined;
        const byId = new Map<number, NameEntry>();
        const byMember = new Map<string, NameEntry>();
        for (const e of data.names) {
            if (typeof e?.id !== "number" || typeof e.name !== "string") continue;
            byId.set(e.id, e);
            if (typeof e.member === "string") byMember.set(e.member, e);
        }
        return { table: data.table ?? "", type: data.type ?? "", language: data.language ?? "", byId, byMember };
    } catch {
        return undefined;
    }
}

export function namesFilePath(root: string, language: string, type: string): string | undefined {
    const table = NAME_TABLES[type];
    return table ? path.join(root, language, `${table}.json`) : undefined;
}

/** The entry a use refers to, if the file has one. */
export function lookupName(file: NamesFile | undefined, use: NameUse): NameEntry | undefined {
    if (!file) return undefined;
    return use.member !== undefined ? file.byMember.get(use.member) : use.id !== undefined ? file.byId.get(use.id) : undefined;
}

/** The nearest `.atel/names` folder at or above a file's folder; undefined when there is none. */
export function findNamesRoot(filePath: string, exists: (p: string) => boolean = fs.existsSync): string | undefined {
    let dir = path.dirname(filePath);
    for (;;) {
        const candidate = path.join(dir, ".atel", "names");
        if (exists(candidate)) return candidate;
        const parent = path.dirname(dir);
        if (parent === dir) return undefined;
        dir = parent;
    }
}
