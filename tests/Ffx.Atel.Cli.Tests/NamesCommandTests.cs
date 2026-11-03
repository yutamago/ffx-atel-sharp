// SPDX-License-Identifier: MIT

namespace Ffx.Atel.Cli.Tests;

/// <summary><c>atel names export</c>'s file: entries by id with the member of the type, made-up names.</summary>
public sealed class NamesCommandTests {
    [Fact]
    public void EntriesGetTheMemberOfTheirIdSortedById() {
        List<NamesCommand.Entry> entries = NamesCommand.Members(typeof(Ffx.Command), [(0x3001, "Made-up item command"), (0x3000, "Made-up attack"), (0xa000, "A key item")]);
        Assert.Equal([0x3000, 0x3001, 0xa000], entries.Select(e => e.Id));
        Assert.Equal(["Attack", "Item", null], entries.Select(e => e.Member));
        // A class of int constants as well as an enum.
        Assert.Equal("Kami00_00", NamesCommand.Members(typeof(Ffx.Battle), [(0x012c0000, "x")])[0].Member);
    }

    [Fact]
    public void WritesTheDocumentedShape() {
        string json = NamesCommand.Json(new NamesCommand.NamesFile("commands", "Command", "en",
            NamesCommand.Members(typeof(Ffx.Command), [(0x3000, "Made-up \"attack\""), (0xa000, "Ünïcode")])));
        Assert.Equal("""
            {
              "table": "commands",
              "type": "Command",
              "language": "en",
              "names": [
                {
                  "id": 12288,
                  "member": "Attack",
                  "name": "Made-up \"attack\""
                },
                {
                  "id": 40960,
                  "name": "Ünïcode"
                }
              ]
            }

            """.ReplaceLineEndings("\n"), json);
    }
}
