// SPDX-License-Identifier: MIT
//
// Quick documentation on Text[n] (Ctrl+Q, and Rider's documentation on mouse hover): the whole message of each script
// the file belongs to, like the VS Code extension's hover. Asked before the C# providers, which would document the
// Text indexer itself.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using JetBrains.Application.DataContext;
using JetBrains.Application.Parts;
using JetBrains.Application.Settings;
using JetBrains.DocumentModel;
using JetBrains.DocumentModel.DataContext;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.QuickDoc;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.ReSharper.Psi.Files;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.TextControl;
using JetBrains.TextControl.DataContext;
using JetBrains.UI.RichText;

namespace Ffx.Atel.Rider
{
    [QuickDocProvider(-100, Instantiation.DemandAnyThreadSafe)]
    public sealed class TextQuickDocProvider : IQuickDocProvider
    {
        private readonly ISolution mySolution;
        private readonly TextTables myTables;
        private readonly ISettingsStore mySettings;

        public TextQuickDocProvider(ISolution solution, TextTables tables, ISettingsStore settings)
        {
            mySolution = solution;
            myTables = tables;
            mySettings = settings;
        }

        public bool CanNavigate(IDataContext context) => Describe(context) != null;

        public void Resolve(IDataContext context, Action<IQuickDocPresenter, PsiLanguageType> resolved)
        {
            var html = Describe(context);
            if (html != null) resolved(new TextQuickDocPresenter(html.Value.Title, html.Value.Body), CSharpLanguage.Instance);
        }

        /// <summary>The documentation of the <c>Text[n]</c> at the caret (or under the mouse); null when there is none.</summary>
        private (string Title, string Body)? Describe(IDataContext context)
        {
            // The editor at the caret: a text control (Ctrl+Q, the hover), else an editor context.
            IDocument document;
            DocumentOffset offset;
            var textControl = context.GetData(TextControlDataConstants.TEXT_CONTROL);
            var editor = context.GetData(DocumentModelDataConstants.EDITOR_CONTEXT);
            if (textControl != null)
            {
                document = textControl.Document;
                offset = textControl.Caret.DocumentOffset();
            }
            else if (editor != null)
            {
                document = editor.Document;
                offset = editor.CaretOffset;
            }
            else return null;

            var sourceFile = document.GetPsiSourceFile(mySolution);
            if (sourceFile == null) return null;
            var file = sourceFile.GetPrimaryPsiFile();
            if (file == null || !file.Language.Is<CSharpLanguage>()) return null;
            // The caret may be just after the closing bracket.
            var node = file.FindNodeAt(new DocumentRange(offset));
            var index = -1;
            if ((node == null || TextUses.Containing(node, out index) == null) && offset.Offset > 0)
            {
                node = file.FindNodeAt(new DocumentRange(offset - 1));
                if (node == null || TextUses.Containing(node, out index) == null) return null;
            }

            var settings = mySettings.BindToContextTransient(ContextRange.Smart(document.ToDataContext2()));
            var textContext = myTables.ContextOf(sourceFile, settings);
            return Html(textContext, myTables.Find(textContext, index), index);
        }

        public static (string Title, string Body)? Html(TextContext context, IReadOnlyList<Lookup> lookups, int index)
        {
            if (context.Scripts.Count == 0) return null;
            var title = $"Message {index}";
            var body = new StringBuilder();
            if (context.Root == null)
            {
                body.Append("<p>No message text found. Run <code>atel text export -o &lt;source dir&gt;</code> (it writes ")
                    .Append("<code>.atel/text</code>), or set the text folder in Settings → Tools → Atel# message text.</p>");
                return (title, body.ToString());
            }

            var languages = context.TextLanguages;
            var lang = WebUtility.HtmlEncode(context.Language);
            if (!languages.Contains(context.Language))
            {
                body.Append($"<p>No <code>{lang}</code> text (<code>atel text export --lang {lang}</code> exports it)");
                if (languages.Count > 0) body.Append("; exported: ").Append(WebUtility.HtmlEncode(string.Join(", ", languages)));
                body.Append(". The language is in Settings → Tools → Atel# message text.</p>");
                return (title, body.ToString());
            }

            var shown = 0;
            foreach (var lookup in lookups)
            {
                if (shown++ == 8) break;
                var name = WebUtility.HtmlEncode(lookup.Script.Name);
                if (lookup.Found && lookup.Text != null)
                {
                    var (speaker, lines) = TextResolver.MessageParts(lookup.Text);
                    var by = speaker != null ? ": " + WebUtility.HtmlEncode(speaker) : "";
                    body.Append($"<p><b>{name}</b>, message {index} ({lang}){by}</p>");
                    body.Append("<pre>").Append(WebUtility.HtmlEncode(string.Join("\n", lines))).Append("</pre>");
                    continue;
                }

                body.Append($"<p><b>{name}</b>, message {index} ({lang})</p>");
                if (lookup.Script.Category == ScriptCategory.Monster)
                    body.Append("<p><i>A monster has no message table: the index is into the current battle's.</i></p>");
                else if (lookup.Found)
                    body.Append("<p><i>No text in this message.</i></p>");
                else
                    body.Append($"<p><i>No message {index} for {name} in <code>{lang}</code>.</i></p>");
            }

            // The other languages there are, as the VS Code hover's links (here the setting switches them).
            if (languages.Count > 1)
                body.Append("<p>Languages: ")
                    .Append(string.Join(", ", languages.Select(l => l == context.Language ? $"<b>{WebUtility.HtmlEncode(l)}</b>" : WebUtility.HtmlEncode(l))))
                    .Append(" (Settings → Tools → Atel# message text)</p>");
            return (title, body.ToString());
        }
    }

    public sealed class TextQuickDocPresenter : IQuickDocPresenter
    {
        private readonly string myTitle;
        private readonly string myBody;

        public TextQuickDocPresenter(string title, string body)
        {
            myTitle = title;
            myBody = body;
        }

        public QuickDocTitleAndText GetHtml(PsiLanguageType presentationLanguage) =>
            new QuickDocTitleAndText(new RichText("<html><body>" + myBody + "</body></html>"), new RichText(myTitle));

        public string GetId() => null;
        public IQuickDocPresenter Resolve(string id) => null;
        public void OpenInEditor(string navigationId = "") { }
        public void ReadMore(string navigationId = "") { }
    }
}
