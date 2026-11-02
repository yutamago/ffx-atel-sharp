// SPDX-License-Identifier: MIT
//
// The inlay hint after Text[n]: a highlighting the daemon adds (TextHintAnalyzer), drawn by an adornment provider,
// with the whole message as its tooltip.

using System.Collections.Generic;
using JetBrains.Application.Parts;
using JetBrains.Application.UI.Controls.BulbMenu.Items;
using JetBrains.Application.UI.Controls.Utils;
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

        public TextHint(ITreeNode node, DocumentOffset offset, string label, string toolTip)
        {
            myNode = node;
            myOffset = offset;
            Label = label;
            ToolTip = toolTip;
        }

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
        public bool IsValid(IHighlighter highlighter) => highlighter.UserData is TextHint;

        public IAdornmentDataModel CreateDataModel(IHighlighter highlighter) =>
            highlighter.UserData is TextHint hint ? new TextHintDataModel(hint.Label) : null;
    }

    public sealed class TextHintDataModel : IAdornmentDataModel
    {
        public TextHintDataModel(string label)
        {
            Data = new AdornmentData(new RichText(label), null, default,
                AdornmentPlacement.DefaultAfterPrevChar, PushToHintMode.Default);
        }

        public AdornmentData Data { get; }
        public IPresentableItem ContextMenuTitle => null;
        public IEnumerable<BulbMenuItem> ContextMenuItems => EmptyList<BulbMenuItem>.Instance;
        public TextRange? SelectionRange => null;
        public void ExecuteNavigation(PopupWindowContextSource popupWindowContextSource) { }
    }
}
