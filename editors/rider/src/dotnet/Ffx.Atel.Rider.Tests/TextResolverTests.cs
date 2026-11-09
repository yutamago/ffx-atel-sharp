// SPDX-License-Identifier: MIT
//
// The resolution rules without the IDE, the same cases as the VS Code extension's resolve.test.ts. Made-up names and
// text only.

using System.IO;
using System.Linq;
using System.Text;
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
            Assert.That(TextResolver.HintLabel("One\nTwo", 60), Is.EqualTo("One Two"));
            Assert.That(TextResolver.HintLabel("abcdefghij", 5), Is.EqualTo("abcd…"));
            // Cut at the last space in the second half when the cut is inside a word.
            Assert.That(TextResolver.HintLabel("A made-up line that is long", 20), Is.EqualTo("A made-up line that…"));
            Assert.That(TextResolver.HintLabel("Abcdefghijklmnop qrstuvwxyz", 20), Is.EqualTo("Abcdefghijklmnop…"));
            Assert.That(TextResolver.Quote("say \"hi\"\\"), Is.EqualTo("\"say \\\"hi\\\"\\\\\""));
        }

        private static string Parts(string text)
        {
            var (speaker, lines) = TextResolver.MessageParts(text);
            return (speaker ?? "-") + "|" + string.Join("/", lines);
        }

        [Test]
        public void SplitsAMessageIntoSpeakerAndLines()
        {
            Assert.That(Parts("{name:Tidus}\nMade-up line.\nAnd another."), Is.EqualTo("Tidus|Made-up line./And another."));
            Assert.That(Parts("{wait:0}{name:Wakka}\r\nMade-up."), Is.EqualTo("Wakka|Made-up."));
            // A first line left empty: a speaker the window does not name.
            Assert.That(Parts("\nMade-up question?"), Is.EqualTo("-|Made-up question?"));
            // Anything else on the first line is text: a plain name, a choice, codes.
            Assert.That(Parts("Someone\nMade-up."), Is.EqualTo("-|Someone/Made-up."));
            Assert.That(Parts("{wait:0}\nMade-up."), Is.EqualTo("-|{wait:0}/Made-up."));
            Assert.That(Parts("{name:Tidus}"), Is.EqualTo("-|{name:Tidus}"));
            Assert.That(Parts("{name:Tidus} and {name:Yuna}\nMade-up."), Is.EqualTo("-|{name:Tidus} and {name:Yuna}/Made-up."));

            Assert.That(TextResolver.MessageHint("{name:Tidus}\nMade-up line.\nAnd \"another\".", 60), Is.EqualTo("Tidus: \"Made-up line. And \\\"another\\\".\""));
            Assert.That(TextResolver.MessageHint("\nMade-up question?", 60), Is.EqualTo("\"Made-up question?\""));
            Assert.That(TextResolver.MessageFull("{name:Tidus}\nMade-up line.\nAnd another."), Is.EqualTo("Tidus:\nMade-up line.\nAnd another."));
            Assert.That(TextResolver.MessageFull("\nMade-up question?"), Is.EqualTo("Made-up question?"));
        }

        [Test]
        public void ReadsOnlyTheStartOfABigFile()
        {
            var filler = "// " + new string('x', 100) + "\n";
            var big = new StringBuilder("namespace Events.Aa.Aaaa0100;\n");
            while (big.Length < TextResolver.HeadLength * 4) big.Append(filler);
            big.Append("[Event(\"zzzz9999\")]\n");
            Assert.That(Scripts(big.ToString(), "/x.cs"), Is.EqualTo(new[] { "aaaa0100:Event" }));
        }

        [Test]
        public void ListsTheExportedLanguages()
        {
            var root = Path.Combine(Path.GetTempPath(), "atel-languages-" + Path.GetRandomFileName());
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "en"));
                Directory.CreateDirectory(Path.Combine(root, "de"));
                Assert.That(TextResolver.ExportedLanguages(root), Is.EqualTo(new[] { "de", "en" }));
                Assert.That(TextResolver.ExportedLanguages(Path.Combine(root, "missing")), Is.Empty);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }
    }
}
