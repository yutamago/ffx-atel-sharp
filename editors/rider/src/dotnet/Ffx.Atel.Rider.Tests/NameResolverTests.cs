// SPDX-License-Identifier: MIT
//
// The names rules without the IDE, the cases of the VS Code extension's resolve.test.ts. Made-up names only.

using System.IO;
using NUnit.Framework;

namespace Ffx.Atel.Rider.Tests
{
    [TestFixture]
    public class NameResolverTests
    {
        [Test]
        public void ReadsANamesFileAndLooksUpMembersAndIds()
        {
            var file = NameResolver.ParseNamesFile("{\"table\":\"commands\",\"type\":\"Command\",\"language\":\"en\",\"names\":["
                + "{\"id\":12288,\"member\":\"Bbbb\",\"name\":\"Made-up command\"},{\"id\":40960,\"name\":\"A made-up key item\"}]}");
            Assert.That(file.Find("Bbbb", null).Name, Is.EqualTo("Made-up command"));
            Assert.That(file.Find(null, 0xa000).Name, Is.EqualTo("A made-up key item"));
            Assert.That(file.Find("Zzzz", null), Is.Null);
            Assert.That(NameResolver.ParseNamesFile("{}"), Is.Null);
            Assert.That(NameResolver.ParseNamesFile("not json"), Is.Null);
            Assert.That(NameResolver.NamesFilePath("/r", "de", "Target"), Is.EqualTo(Path.Combine("/r", "de", "monsters.json")));
            Assert.That(NameResolver.NamesFilePath("/r", "de", "Model"), Is.Null);
            Assert.That(NameResolver.Describe("Command", file.Find(null, 0xa000), "en"), Is.EqualTo("(Command)0xa000 (0xa000, en): A made-up key item"));
            Assert.That(NameResolver.Describe("Command", file.Find("Bbbb", null), "en"), Is.EqualTo("Command.Bbbb (0x3000, en): Made-up command"));
        }

        [Test]
        public void ParsesIntegerLiterals()
        {
            Assert.That(NameResolver.ParseInteger("4097"), Is.EqualTo(4097));
            Assert.That(NameResolver.ParseInteger("0x1001"), Is.EqualTo(0x1001));
            Assert.That(NameResolver.ParseInteger("0X1001"), Is.EqualTo(0x1001));
            Assert.That(NameResolver.ParseInteger("1.5"), Is.Null);
            Assert.That(NameResolver.ParseInteger("-1"), Is.Null);
        }

        [Test]
        public void FindsTheNearestNamesFolder()
        {
            var root = Path.Combine("/src", ".atel", "names");
            Assert.That(NameResolver.FindNamesRoot(Path.Combine("/src", "Events", "Aa", "W01.cs"), p => p == root), Is.EqualTo(root));
            Assert.That(NameResolver.FindNamesRoot(Path.Combine("/src", "W01.cs"), p => false), Is.Null);
        }
    }
}
