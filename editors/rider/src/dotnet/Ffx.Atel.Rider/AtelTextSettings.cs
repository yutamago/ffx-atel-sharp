// SPDX-License-Identifier: MIT

using JetBrains.Application.Settings;
using JetBrains.Application.Settings.WellKnownRootKeys;

namespace Ffx.Atel.Rider
{
    /// <summary>The plugin's settings (Settings → Editor → Atel# message text); the same as the VS Code extension's
    /// <c>atel.*</c> settings.</summary>
    [SettingsKey(typeof(EnvironmentSettings), "Atel# message text")]
    public class AtelTextSettings
    {
        [SettingsEntry("en", "The language whose message text is shown for Text[n]: en, ja, fr, es, de, it, ko or zh")]
        public string Language;

        [SettingsEntry("", "The folder with one subfolder per language; empty: the nearest .atel/text above the file")]
        public string TextDirectory;

        [SettingsEntry(true, "Show the message text after Text[n] as an inlay hint")]
        public bool ShowInlayHints;

        [SettingsEntry(60, "Inlay hints longer than this are cut with an ellipsis")]
        public int MaxLength;
    }
}
