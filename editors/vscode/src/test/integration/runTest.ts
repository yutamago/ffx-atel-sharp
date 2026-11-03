// SPDX-License-Identifier: MIT
//
// `npm run test:integration`: starts a downloaded VS Code (in .vscode-test) with the extension on a made-up
// source tree and checks the inlay hints and the hovers (suite.ts), of a message and of names. On Linux run it under `xvfb-run -a`.

import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";
import { runTests } from "@vscode/test-electron";

async function main(): Promise<void> {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), "atel-text-"));
    const folder = path.join(root, "Events", "Aa", "Aaaa0100");
    fs.mkdirSync(folder, { recursive: true });
    fs.writeFileSync(
        path.join(folder, "W01.cs"),
        [
            "// event/obj/aa/aaaa0100/aaaa0100.ebp, decompiled by atel.",
            "namespace Events.Aa.Aaaa0100;",
            "",
            "public sealed class W01",
            "{",
            "    void Talk()",
            "    {",
            "        Common.MsgSetText(0, Text[1]);",
            "        Btl.UseCommand(Target.Aaaa, (Command)0x3000);",
            "    }",
            "}",
            "",
        ].join("\n"),
    );
    for (const [lang, text] of [
        ["en", "{name:Tidus}\nMade-up line."],
        ["de", "{name:Tidus}\nErfundene Zeile."],
    ]) {
        const dir = path.join(root, ".atel", "text", lang);
        fs.mkdirSync(dir, { recursive: true });
        const file = { script: "aaaa0100", category: "event", language: lang, messages: ["First.", text] };
        fs.writeFileSync(path.join(dir, "aaaa0100.json"), JSON.stringify(file));
        const names = path.join(root, ".atel", "names", lang);
        fs.mkdirSync(names, { recursive: true });
        const monster = lang === "en" ? "Made-up monster" : "Erfundenes Monster";
        const monsters = { table: "monsters", type: "Target", language: lang, names: [{ id: 0x1001, member: "Aaaa", name: monster }] };
        fs.writeFileSync(path.join(names, "monsters.json"), JSON.stringify(monsters));
        const commands = { table: "commands", type: "Command", language: lang, names: [{ id: 0x3000, member: "Bbbb", name: "Made-up command" }] };
        fs.writeFileSync(path.join(names, "commands.json"), JSON.stringify(commands));
    }
    try {
        await runTests({
            // A VS Code already on disk (VSCODE_EXECUTABLE), else one downloaded into .vscode-test.
            vscodeExecutablePath: process.env.VSCODE_EXECUTABLE || undefined,
            extensionDevelopmentPath: path.resolve(__dirname, "../../.."),
            extensionTestsPath: path.resolve(__dirname, "suite"),
            // A fresh profile each run: the suite changes atel.textLanguage.
            launchArgs: [root, "--user-data-dir", path.join(root, ".user-data"), "--disable-extensions", "--disable-workspace-trust", "--skip-welcome", "--skip-release-notes"],
            extensionTestsEnv: { ATEL_TEST_FILE: path.join(folder, "W01.cs") },
        });
    } finally {
        fs.rmSync(root, { recursive: true, force: true });
    }
}

main().catch((e) => {
    console.error(e);
    process.exit(1);
});
