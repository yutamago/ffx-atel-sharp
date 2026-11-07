// SPDX-License-Identifier: MIT

using System;

namespace Ffx.Frames;

/// <summary>The hotkeys' time scale changes.</summary>
public static class TimeScaleSteps {
    /// <summary>The scales Ctrl+PageUp and Ctrl+PageDown step through.</summary>
    public static readonly double[] Steps = [0.05, 0.1, 0.25, 0.5, 1, 2, 3, 4, 6, 8, 10, 16, 25, 50, 100];

    /// <summary>The next step above <paramref name="scale"/> (or the last).</summary>
    public static double Up(double scale) {
        foreach (double s in Steps) if (s > scale * (1 + 1e-9)) return s;
        return Steps[^1];
    }

    /// <summary>The next step below <paramref name="scale"/> (or the first).</summary>
    public static double Down(double scale) {
        for (int i = Steps.Length - 1; i >= 0; i--) if (Steps[i] < scale * (1 - 1e-9)) return Steps[i];
        return Steps[0];
    }

    /// <summary>A toggle: <paramref name="preset"/> unless the scale is already there, then ×1.</summary>
    public static double Toggle(double scale, double preset) => Math.Abs(scale - preset) < 1e-9 ? 1 : preset;
}
