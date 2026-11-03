// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Linq;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Tree;

namespace Ffx.Atel.Rider
{
    /// <summary>An inlay hint after each <c>Text[n]</c> with the message: the first script's, <c>(+n)</c> when the
    /// other users of a library class have different ones.</summary>
    [ElementProblemAnalyzer(typeof(IElementAccessExpression), HighlightingTypes = new[] { typeof(TextHint) })]
    public sealed class TextHintAnalyzer : ElementProblemAnalyzer<IElementAccessExpression>
    {
        private readonly TextTables myTables;

        public TextHintAnalyzer(TextTables tables) => myTables = tables;

        protected override void Run(IElementAccessExpression element, ElementProblemAnalyzerData data, IHighlightingConsumer consumer)
        {
            if (!TextUses.TryGetIndex(element, out var index) || data.SourceFile == null) return;
            var tables = myTables;
            var context = data.GetOrCreateDataNoLock<TextContext>(TextTables.ContextKey, () => tables.ContextOf(data.SourceFile, data.SettingsStore));
            if (!context.ShowHints || context.Root == null || context.Scripts.Count == 0) return;

            var found = tables.Find(context, index).Where(l => l.Found && l.Text != null).ToList();
            if (found.Count == 0) return;
            var others = found.Select(l => l.Text).Distinct().Count() - 1;
            var label = TextResolver.Quote(TextResolver.HintLabel(found[0].Text, context.MaxLength)) + (others > 0 ? $" (+{others})" : "");
            var toolTip = found.Count == 1
                ? found[0].Text
                : string.Join("\n\n", found.Select(l => $"{l.Script.Name}: {l.Text}"));
            consumer.AddHighlighting(new TextHint(element, element.GetDocumentEndOffset(), label, toolTip));
        }
    }
}
