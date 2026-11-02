// SPDX-License-Identifier: MIT

using Ffx.Atel.Bytecode;
using Ffx.Atel.Bytecode.Script;
using Ffx.Atel.Bytecode.Text;
using Ffx.Atel.Bytecode.Vbf;
using Ffx.Atel.Decompiler;
using Ffx.Atel.Decompiler.Layout;
using Microsoft.CodeAnalysis;

namespace Ffx.Atel.Compiler.Tests;

/// <summary><c>atel decompile --text-comments</c>: comments only. Needs <c>FFX_GAME_DIR</c>; skipped without it.</summary>
public sealed class TextCommentTests(ITestOutputHelper output) {
    /// <summary>
    ///     Some event and battle scripts with message tables, decompiled with English comments after their
    ///     <c>Text[n]</c>, compile back to the game's bytes.
    /// </summary>
    [Fact]
    public void CommentedSourceCompilesToTheSameBytes() {
        string? dir = GameData.GameDir();
        if (dir is null) Assert.Skip($"{GameData.GameDirVariable} is not set");
        using VbfArchive archive = GameData.OpenArchive(dir!);
        ScriptMessages messages = new(archive);
        List<string> scripts = [
            .. GameData.ScriptPaths(archive).Where(p => p.StartsWith(GameData.EventPrefix, StringComparison.Ordinal)
                && archive.Find(GameLanguage.English.EventMessagesPath(GameData.Identify(p)!.Value.Name)) is not null).Take(4),
            .. GameData.BattlePaths(archive).Where(p => archive.Find(GameLanguage.English.BattleMessagesPath(GameData.Identify(p)!.Value.Name)) is not null).Take(2),
        ];
        Assert.Equal(6, scripts.Count);
        int comments = 0, uses = 0;
        foreach (string path in scripts) {
            byte[] original = archive.Read(path);
            ScriptFile vanilla = ScriptFile.Parse(original);
            DecompiledScript decompiled = ScriptDecompiler.Decompile(path, vanilla);
            SharedLibrary alone = new([decompiled.Parts], []);
            IReadOnlyList<string?> texts = messages.Display(path, GameLanguage.English)!;
            List<SourceFile> sources = [.. SourceLayout.Files(alone.Scripts[0]).Select(f => f with { Text = TextComments.Comment(f.Text, texts) })];
            comments += sources.Sum(f => f.Text.Split(" // ").Length - 1);
            uses += sources.Sum(f => f.Text.Split("Text[").Length - 1);
            List<SyntaxTree> trees = [.. sources.Select(s => ScriptCompiler.Parse(s.Text, s.RelativePath))];
            byte[] rebuilt = ScriptCompiler.Compile(trees, ScriptLibrary.Empty).ApplyTo(vanilla).ToArray();
            Assert.True(rebuilt.AsSpan().SequenceEqual(original), $"{path} differs");
        }
        output.WriteLine($"{scripts.Count} scripts: {uses} Text[n], {comments} comments");
        Assert.True(comments > 0);
    }
}
