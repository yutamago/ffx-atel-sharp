// SPDX-License-Identifier: MIT
//
// The inlay hint after Text[n] (and after the typed values with a name): a highlighting the daemon adds
// (TextHintAnalyzer, NameHints), drawn by an adornment provider, with the whole message as its tooltip. Its context
// menu switches the language among those the export has, opens the settings, or hides the hints.

using System.Collections.Generic;
using System.Linq;
using JetBrains.Application.Parts;
using JetBrains.Application.Settings;
using JetBrains.Application.UI.Controls.BulbMenu.Anchors;
using JetBrains.Application.UI.Controls.BulbMenu.Items;
using JetBrains.Application.UI.Controls.Utils;
using JetBrains.Application.UI.Icons.CommonThemedIcons;
using JetBrains.Application.UI.PopupLayout;
using JetBrains.DocumentModel;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Daemon;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Feature.Services.InlayHints;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.TextControl.DocumentMarkup;
using JetBrains.TextControl.DocumentMarkup.Adornments;
using JetBrains.UI.Icons;
using JetBrains.UI.RichText;
using JetBrains.Util;

namespace Ffx.Atel.Rider
{
    [RegisterHighlighterGroup(GroupId, "Atel# message text", HighlighterGroupPriority.CODE_SETTINGS)]
    [RegisterHighlighter(
        AttributeId,
        GroupId = GroupId,
        ForegroundColor = "#707070",
        BackgroundColor = "#EBEBEB",
        DarkForegroundColor = "#787878",
        DarkBackgroundColor = "#3B3B3C",
        EffectType = EffectType.INTRA_TEXT_ADORNMENT,
        Layer = HighlighterLayer.ADDITIONAL_SYNTAX,
        TransmitUpdates = true)]
    [DaemonAdornmentProvider(typeof(TextHintAdornmentProvider))]
    [DaemonTooltipProvider(typeof(InlayHintTooltipProvider))]
    [StaticSeverityHighlighting(Severity.INFO, typeof(HighlightingGroupIds.CodeInsights), AttributeId = AttributeId,
        OverlapResolve = OverlapResolveKind.NONE, ShowToolTipInStatusBar = false)]
    public sealed class TextHint : IInlayHintWithDescriptionHighlighting
    {
        public const string GroupId = "Atel# Message Text";
        public const string AttributeId = "Atel# Message Text Hint";

        private readonly ITreeNode myNode;
        private readonly DocumentOffset myOffset;

        public TextHint(ITreeNode node, DocumentOffset offset, string label, string toolTip, string language = null,
            IReadOnlyList<string> languages = null)
        {
            myNode = node;
            myOffset = offset;
            Label = label;
            ToolTip = toolTip;
            Language = language;
            Languages = languages ?? new string[0];
        }

        /// <summary>The language shown.</summary>
        public string Language { get; }

        /// <summary>The languages the export has, for the context menu.</summary>
        public IReadOnlyList<string> Languages { get; }

        /// <summary>What the hint shows: the message on one line, shortened.</summary>
        public string Label { get; }

        /// <summary>The whole message (each script's, for a library class).</summary>
        public string ToolTip { get; }

        public string ErrorStripeToolTip => ToolTip;
        public RichText Description => "Atel# message text";
        public bool IsValid() => myNode.IsValid();
        public DocumentRange CalculateRange() => new DocumentRange(myOffset);
    }

    [SolutionComponent(Instantiation.ContainerAsyncAnyThreadSafe)]
    public sealed class TextHintAdornmentProvider : IHighlighterAdornmentProvider
    {
        private readonly ISettingsStore mySettings;

        public TextHintAdornmentProvider(ISettingsStore settings) => mySettings = settings;

        public bool IsValid(IHighlighter highlighter) => highlighter.UserData is TextHint;

        public IAdornmentDataModel CreateDataModel(IHighlighter highlighter) =>
            highlighter.UserData is TextHint hint ? new TextHintDataModel(hint.Label, TextHintMenu.Items(mySettings, hint.Language, hint.Languages)) : null;
    }

    /// <summary>The hint's context menu: the export's languages (the shown one checked), the settings page, hiding
    /// the hints. The language goes where Rider's own hint menus write, the machine's settings.</summary>
    public static class TextHintMenu
    {
        public static IReadOnlyList<BulbMenuItem> Items(ISettingsStore settings, string language, IReadOnlyList<string> languages)
        {
            var items = new List<BulbMenuItem>();
            if (languages.Count > 1)
            {
                var submenu = new SubmenuAnchor(BulbMenuAnchors.FirstClassContextItems, SubmenuBehavior.Static(new RichText("Message language")));
                foreach (var code in languages)
                {
                    var name = TextResolver.Languages.FirstOrDefault(l => l.Key == code).Value;
                    items.Add(new BulbMenuItem(
                        new ExecutableItem(() => settings.BindToContextTransient(ContextRange.ApplicationWide).SetValue((AtelTextSettings s) => s.Language, code)),
                        new RichText(name != null ? $"{code} ({name})" : code),
                        code == language ? CommonThemedIcons.MenuItemCheck.Id : null,
                        submenu));
                }
            }

            items.Add(IntraTextAdornmentDataModelHelper.CreateConfigureBulbMenuItem(AtelTextOptionsPage.Pid));
            items.Add(IntraTextAdornmentDataModelHelper.CreateTurnOffBulbMenuItem<AtelTextSettings>(settings, s => s.ShowInlayHints,
                "Hide Atel# message text hints", null, BulbMenuAnchors.SecondClassContextItems));
            return items;
        }
    }

    public sealed class TextHintDataModel : IAdornmentDataModel
    {
        public TextHintDataModel(string label, IEnumerable<BulbMenuItem> menu = null)
        {
            Data = new AdornmentData(new RichText(label), null, default,
                AdornmentPlacement.DefaultAfterPrevChar, PushToHintMode.Default);
            ContextMenuItems = menu ?? EmptyList<BulbMenuItem>.Instance;
        }

        public AdornmentData Data { get; }
        public IPresentableItem ContextMenuTitle => null;
        public IEnumerable<BulbMenuItem> ContextMenuItems { get; }
        public TextRange? SelectionRange => null;
        public void ExecuteNavigation(PopupWindowContextSource popupWindowContextSource) { }
    }
}
