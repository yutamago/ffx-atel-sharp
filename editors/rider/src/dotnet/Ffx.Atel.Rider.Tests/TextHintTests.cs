// SPDX-License-Identifier: MIT
//
// The inlay hints, through ReSharper's daemon on made-up sources and text (test/data/Hints, with its .atel/text): the
// gold files list each hint's place and tooltip, then the labels the hints show.

using System.Collections.Generic;
using System.IO;
using System.Threading;
using JetBrains.Application.Settings;
using JetBrains.ReSharper.Feature.Services.Daemon;
using JetBrains.ReSharper.FeaturesTestFramework.Daemon;
using JetBrains.ReSharper.Psi;
using NUnit.Framework;

namespace Ffx.Atel.Rider.Tests
{
    public abstract class TextHintTestBase : CSharpHighlightingTestBase
    {
        private readonly List<string> myLabels = new List<string>();

        protected override string RelativeTestDataPath => "Hints";

        protected override bool HighlightingPredicate(IHighlighting highlighting, IPsiSourceFile sourceFile,
            IContextBoundSettingsStore settingsStore)
        {
            if (!(highlighting is TextHint hint)) return false;
            if (!myLabels.Contains(hint.Label)) myLabels.Add(hint.Label);
            return true;
        }

        protected override void DumpHighlightings(IPsiSourceFile sourceFile, TextWriter goldWriter)
        {
            myLabels.Clear();
            base.DumpHighlightings(sourceFile, goldWriter);
            goldWriter.WriteLine("Labels:");
            foreach (var label in myLabels) goldWriter.WriteLine("  " + label);
        }
    }

    [TestFixture, Apartment(ApartmentState.STA)]
    public class TextHintTests : TextHintTestBase
    {
        [Test] public void TestEvent() => DoNamedTest2();
        [Test] public void TestLibrary() => DoNamedTest2();
        [Test] public void TestAttribute() => DoNamedTest2();
    }

    [TestFixture, Apartment(ApartmentState.STA)]
    public class GermanTextHintTests : TextHintTestBase
    {
        protected override void SetupSettings()
        {
            base.SetupSettings();
            SettingsStore.SetValue((AtelTextSettings s) => s.Language, "de");
            SettingsStore.SetValue((AtelTextSettings s) => s.MaxLength, 12);
        }

        [Test] public void TestGerman() => DoNamedTest2();
    }

    [TestFixture, Apartment(ApartmentState.STA)]
    public class HintsOffTests : TextHintTestBase
    {
        protected override void SetupSettings()
        {
            base.SetupSettings();
            SettingsStore.SetValue((AtelTextSettings s) => s.ShowInlayHints, false);
        }

        [Test] public void TestHintsOff() => DoNamedTest2();
    }
}
