// SPDX-License-Identifier: MIT

using System.Linq;
using JetBrains.Application.UI.Options;
using JetBrains.Application.UI.Options.OptionsDialog;
using JetBrains.IDE.UI.Options;
using JetBrains.Lifetimes;

namespace Ffx.Atel.Rider
{
    /// <summary>Settings → Tools → Atel# message text. The frontend registers the page under the same id
    /// (AtelTextOptionsPage.kt, plugin.xml).</summary>
    [OptionsPage(Pid, "Atel# message text", null)]
    public sealed class AtelTextOptionsPage : BeSimpleOptionsPage
    {
        public const string Pid = "AtelTextOptionsPage";

        public AtelTextOptionsPage(Lifetime lifetime, OptionsPageContext optionsPageContext,
            OptionsSettingsSmartContext optionsSettingsSmartContext)
            : base(lifetime, optionsPageContext, optionsSettingsSmartContext)
        {
            AddComboOption((AtelTextSettings s) => s.Language, TextResolver.Languages.Select(l => l.Key),
                code => code + " (" + TextResolver.Languages.First(l => l.Key == code).Value + ")", null, "Language:");
            AddTextBox((AtelTextSettings s) => s.TextDirectory, "Text folder (empty: the nearest .atel/text above the file):");
            AddBoolOption((AtelTextSettings s) => s.ShowInlayHints, "Show the message after Text[n] as an inlay hint");
            AddComboOption((AtelTextSettings s) => s.MaxLength, new[] { 20, 30, 40, 60, 80, 120, 200 },
                n => n.ToString(), null, "Shorten inlay hints to:", "characters");
            AddCommentText("The text comes from `atel text export`, which writes .atel/text/<language>/<script>.json beside the decompiled source.");
        }
    }
}
