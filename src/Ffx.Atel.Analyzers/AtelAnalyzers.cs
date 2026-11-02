// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Ffx.Atel.Analyzers;

/// <summary>The analyzers that make C# into Atel, for hosts that attach them by hand (<c>atel compile --check</c>).</summary>
public static class AtelAnalyzers {
    public static ImmutableArray<DiagnosticAnalyzer> All { get; } = [
        new SubsetAnalyzer(), new RequestAnalyzer(), new CallAnalyzer(), new RegisterAnalyzer(), new InlineAnalyzer(),
    ];
}
