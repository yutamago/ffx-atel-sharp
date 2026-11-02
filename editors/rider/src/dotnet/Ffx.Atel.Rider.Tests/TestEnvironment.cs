// SPDX-License-Identifier: MIT

using System.Threading;
using JetBrains.Application.BuildScript.Application.Zones;
using JetBrains.ReSharper.Feature.Services;
using JetBrains.ReSharper.Psi.CSharp;
using JetBrains.ReSharper.TestFramework;
using JetBrains.TestFramework;
using JetBrains.TestFramework.Application.Zones;
using NUnit.Framework;

[assembly: Apartment(ApartmentState.STA)]

namespace Ffx.Atel.Rider.Tests
{
    [ZoneDefinition]
    public class AtelTextTestEnvironmentZone : ITestsEnvZone, IRequire<PsiFeatureTestZone>
    {
    }

    [ZoneMarker]
    public class ZoneMarker : IRequire<ICodeEditingZone>, IRequire<ILanguageCSharpZone>, IRequire<AtelTextTestEnvironmentZone>
    {
    }

    // STA on the fixtures themselves as well: off Windows the assembly's apartment is not inherited.
    [SetUpFixture, Apartment(ApartmentState.STA)]
    public class AtelTextTestsAssembly : ExtensionTestEnvironmentAssembly<AtelTextTestEnvironmentZone>
    {
    }
}
