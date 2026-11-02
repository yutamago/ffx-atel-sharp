// SPDX-License-Identifier: MIT
//
// The resolution rules without the IDE, the same cases as the VS Code extension's resolve.test.ts. Made-up names and
// text only.

using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Ffx.Atel.Rider.Tests
{
    [TestFixture]
    public class TextResolverTests
    {
        private static string[] Scripts(string text, string path) =>
            TextResolver.ScriptsOf(text, path).Select(s => s.Name + ":" + s.Category).ToArray();

        [Test]
        public void ResolvesTheScriptFromTheDecompilersFirstLine()
        {
            var text = "// event/obj/aa/aaaa0100/aaaa0100.ebp, decompiled by atel.\nusing Ffx;\nnamespace Events.Aa.Other;\n";
            Assert.That(Scripts(text, "/src/Events/Aa/X/W01.cs"), Is.EqualTo(new[] { "aaaa0100:Event" }));
            Assert.That(Scripts("// battle/btl/zz00_00/zz00_00.bin, decompiled by atel.\n", "/x.cs"), Is.EqualTo(new[] { "zz00_00:Battle" }));
            Assert.That(Scripts("// battle/mon/_m999/m999.bin, decompiled by atel.\r\n", "/x.cs"), Is.EqualTo(new[] { "m999:Monster" }));
        }

        [Test]
        public void ResolvesTheScriptFromItsAttributeTheNamespaceOrTheFolder()
        {
            Assert.That(Scripts("[Event(\"aaaa0100\")]\npublic sealed partial class Aaaa0100 : Script { }", "/x.cs"), Is.EqualTo(new[] { "aaaa0100:Event" }));
            Assert.That(Scripts("namespace Battles.Zz.Zz00_00;\nclass W03 { }", "/x.cs"), Is.EqualTo(new[] { "zz00_00:Battle" }));
            Assert.That(Scripts("namespace Mod;\nclass A { }", Path.Combine("/src", "Aaaa0100", "A.cs")), Is.EqualTo(new[] { "aaaa0100:Unknown" }));
        }

        [Test]
        public void ALibraryClassReadsTheTablesOfItsUsers()
        {
            var text = string.Join("\r\n",
                "// The code of 3 workers, decompiled by atel:",
                "//   Events.Aa.Aaaa0100.W03",
                "//   Events.Aa.Aaaa0100.W07",
                "//   Battles.Zz.Zz00_00.W01",
                "using Ffx;",
                "namespace Library;");
            Assert.That(Scripts(text, "/src/Library/Shared0001.cs"), Is.EqualTo(new[] { "aaaa0100:Event", "zz00_00:Battle" }));
        }

        [Test]
        public void FindsTheNearestTextFolder()
        {
            var root = Path.Combine(Path.DirectorySeparatorChar.ToString(), "src");
            var text = Path.Combine(root, ".atel", "text");
            Assert.That(TextResolver.FindTextRoot(Path.Combine(root, "Events", "Aa", "Aaaa0100", "W01.cs"), p => p == text), Is.EqualTo(text));
            Assert.That(TextResolver.FindTextRoot(Path.Combine(Path.DirectorySeparatorChar.ToString(), "elsewhere", "a.cs"), p => p == text), Is.Null);
            Assert.That(TextResolver.TextFilePath(text, "de", "aaaa0100"), Is.EqualTo(Path.Combine(text, "de", "aaaa0100.json")));
        }

        [Test]
        public void ReadsTextFilesAndShortensHints()
        {
            var file = TextResolver.ParseTextFile("{\"script\":\"aaaa0100\",\"category\":\"event\",\"language\":\"en\",\"messages\":[\"{name:Tidus}\\nHello!\",null]}");
            Assert.That(file.Messages[0], Is.EqualTo("{name:Tidus}\nHello!"));
            Assert.That(file.Messages[1], Is.Null);
            Assert.That(file.Has(1) && !file.Has(2), Is.True);
            Assert.That(TextResolver.ParseTextFile("not json"), Is.Null);
            Assert.That(TextResolver.ParseTextFile("{\"messages\":3}"), Is.Null);
            Assert.That(TextResolver.HintLabel("One\nTwo", 60), Is.EqualTo("One ⏎ Two"));
            Assert.That(TextResolver.HintLabel("abcdefghij", 5), Is.EqualTo("abcd…"));
            Assert.That(TextResolver.Quote("say \"hi\"\\"), Is.EqualTo("\"say \\\"hi\\\"\\\\\""));
        }
    }
}
