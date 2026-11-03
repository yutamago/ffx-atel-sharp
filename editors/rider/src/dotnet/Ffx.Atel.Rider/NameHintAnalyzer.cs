// SPDX-License-Identifier: MIT

using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.Util;

namespace Ffx.Atel.Rider
{
    /// <summary>The inlay hints after the typed values that index the game's name tables: the name, quoted; hovering
    /// the hint shows the value and the name.</summary>
    public static class NameHints
    {
        public static void Add(TextTables tables, ICSharpExpression element, string type, string member, int? id,
            ElementProblemAnalyzerData data, IHighlightingConsumer consumer)
        {
            if (data.SourceFile == null) return;
            var context = data.GetOrCreateDataNoLock<TextContext>(TextTables.ContextKey, () => tables.ContextOf(data.SourceFile, data.SettingsStore));
            if (!context.ShowHints || !context.ShowNameHints || context.NamesRoot == null) return;
            var entry = tables.FindName(context, type, member, id);
            if (entry == null) return;
            var label = TextResolver.Quote(TextResolver.HintLabel(entry.Name, context.MaxLength));
            consumer.AddHighlighting(new TextHint(element, element.GetDocumentEndOffset(), label,
                NameResolver.Describe(type, entry, context.Language)));
        }
    }

    [ElementProblemAnalyzer(typeof(IReferenceExpression), HighlightingTypes = new[] { typeof(TextHint) })]
    public sealed class NameMemberHintAnalyzer : ElementProblemAnalyzer<IReferenceExpression>
    {
        private readonly TextTables myTables;

        public NameMemberHintAnalyzer(TextTables tables) => myTables = tables;

        protected override void Run(IReferenceExpression element, ElementProblemAnalyzerData data, IHighlightingConsumer consumer)
        {
            if (NameUses.TryGetMember(element, out var type, out var member))
                NameHints.Add(myTables, element, type, member, null, data, consumer);
        }
    }

    [ElementProblemAnalyzer(typeof(ICastExpression), HighlightingTypes = new[] { typeof(TextHint) })]
    public sealed class NameCastHintAnalyzer : ElementProblemAnalyzer<ICastExpression>
    {
        private readonly TextTables myTables;

        public NameCastHintAnalyzer(TextTables tables) => myTables = tables;

        protected override void Run(ICastExpression element, ElementProblemAnalyzerData data, IHighlightingConsumer consumer)
        {
            if (NameUses.TryGetCast(element, out var type, out var id))
                NameHints.Add(myTables, element, type, null, id, data, consumer);
        }
    }
}
