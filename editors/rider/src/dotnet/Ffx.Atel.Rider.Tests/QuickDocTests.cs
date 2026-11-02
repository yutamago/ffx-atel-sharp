// SPDX-License-Identifier: MIT
//
// Quick documentation on Text[n] ({caret} in the test file), through the provider and a test text control's data
// context; the gold files hold the title and the HTML. The test copies the file elsewhere, so the text folder is set
// (Settings: text folder) to the test data's .atel/text, except for the case without one.

using System.Threading;
using JetBrains.Application.Components;
using JetBrains.Application.Settings;
using JetBrains.Lifetimes;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.ReSharper.TestFramework;
using JetBrains.ReSharper.TestFramework.PsiServices.ValueTracking;
using NUnit.Framework;

namespace Ffx.Atel.Rider.Tests
{
    [TestFixture, Apartment(ApartmentState.STA)]
    public class QuickDocTests : BaseTestWithTextControl
    {
        protected override string RelativeTestDataPath => "Hints";

        private bool myWithTextFolder = true;

        protected override void DoTest(Lifetime lifetime, IProject testProject)
        {
            var settings = Solution.GetComponent<ISettingsStore>().BindToContextTransient(ContextRange.ApplicationWide);
            var folder = myWithTextFolder ? TestDataPath.Combine(".atel").Combine("text").FullPath : "";
            settings.SetValue((AtelTextSettings s) => s.TextDirectory, folder);
            lifetime.OnTermination(() => settings.ResetValue((AtelTextSettings s) => s.TextDirectory));

            var textControl = OpenTextControl(lifetime);
            var context = DataContextOfTestTextControl.Create(lifetime, textControl, Solution);
            var provider = Solution.GetComponent<TextQuickDocProvider>();
            ExecuteWithGold(writer =>
            {
                if (!provider.CanNavigate(context))
                {
                    writer.WriteLine("(no documentation)");
                    return;
                }

                provider.Resolve(context, (presenter, language) =>
                {
                    var html = presenter.GetHtml(CSharpLanguage.Instance);
                    writer.WriteLine(html.Title.Text);
                    writer.WriteLine(html.Text.Text);
                });
            });
        }

        [Test] public void TestQuickDocEvent() => DoNamedTest2();
        [Test] public void TestQuickDocLibrary() => DoNamedTest2();
        [Test] public void TestQuickDocNotText() => DoNamedTest2();

        [Test]
        public void TestQuickDocNoTable()
        {
            myWithTextFolder = false;
            DoNamedTest2();
        }
    }
}
