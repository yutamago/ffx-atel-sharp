// SPDX-License-Identifier: MIT

using Ffx.Atel;

namespace Ffx;

/// <summary>
///     A line of dialogue as one call. Each method is an inline method (<see cref="InlineAttribute"/>): the
///     compiler writes the message natives below in place of the call, exactly as the game's own scripts
///     have them, so <c>Dialogue.Say(Text[71]);</c> compiles to the same bytes as the 18 lines it stands for.
/// </summary>
/// <remarks>
///     <para>
///         A message window is one of the game's text boxes, numbered from 0 (the shipped scripts use 0-6;
///         0 is the usual one). Its text is a message of the script's table (<c>Text[12]</c>) or, in a mod, a
///         new one (<c>Text.New("…")</c>).
///     </para>
///     <para>
///         The defaults are the values most of the game's lines use: the box centred at the bottom of the
///         screen (x 256, y 448, anchor 4), style 1, mode 0, and the flags of an unvoiced line (12290) or
///         a voiced one (8194). For a voiced line, prepare the voice first (<c>Common.VoiceStandbyEnd(id)</c>,
///         as the game does), then call <see cref="SayVoiced"/>.
///     </para>
///     <para>
///         The arguments are substituted where the natives use them (see <see cref="InlineAttribute"/>):
///         give constants, variables or expressions of them, not calls.
///     </para>
/// </remarks>
public static class Dialogue {
    /// <summary>
    ///     Sets up message window <paramref name="window"/> and opens it with <paramref name="text"/>, without
    ///     waiting: <c>MsgSetPos</c>, <c>MsgSetStyle</c>, <c>MsgSetText</c>, <c>MsgSetFlags</c>, <c>MsgOpen</c>.
    ///     The window stays open until the script closes it (<c>Common.MsgClose(window)</c>).
    /// </summary>
    /// <param name="text">The message to show.</param>
    /// <param name="window">The message window, 0 for the usual one.</param>
    /// <param name="x">Where the window goes on the screen (512 by 448 units), horizontally.</param>
    /// <param name="y">Where the window goes, vertically.</param>
    /// <param name="anchor">Which point of the window <paramref name="x"/> and <paramref name="y"/> place (4 in most lines).</param>
    /// <param name="style">The window's style (0 or 1 in most lines).</param>
    /// <param name="flags">The window's flags (12290 for most unvoiced lines, 8194 for voiced ones, 0 for system messages).</param>
    /// <param name="mode">How the window opens: 0, or 2 for a window with a choice.</param>
    [Inline]
    public static void Open(TextId text, int window = 0, int x = 256, int y = 448, int anchor = 4, int style = 1, int flags = 12290, int mode = 0) {
        Common.MsgSetPos(window, x, y, anchor);
        Common.MsgSetStyle(window, style);
        Common.MsgSetText(window, text);
        Common.MsgSetFlags(window, flags);
        Common.MsgOpen(window, mode);
    }

    /// <summary>Waits until message window <paramref name="window"/> is open (<c>MsgGetState</c> is 2), a frame at a time.</summary>
    /// <param name="window">The message window.</param>
    [Inline(Waits = true)]
    public static void WaitUntilOpen(int window = 0) {
        while (Common.MsgGetState(window) != 2) {
            Common.WaitFrames(1);
        }
    }

    /// <summary>Waits until message window <paramref name="window"/> is ready (<c>MsgIsReady</c> is 1), a frame at a time.</summary>
    /// <param name="window">The message window.</param>
    [Inline(Waits = true)]
    public static void WaitUntilReady(int window = 0) {
        while (true) {
            if (Common.MsgIsReady(window) == 1) {
                break;
            }
            else {
                Common.WaitFrames(1);
            }
        }
    }

    /// <summary>
    ///     A line of dialogue without voice: opens the window with <paramref name="text"/> (<see cref="Open"/>),
    ///     waits until it is ready (<see cref="WaitUntilReady"/>), waits for the player (<c>MsgWaitEnd</c>), then
    ///     closes the window and waits until it is closed (<c>MsgClose</c>, <c>MsgWaitExEnd</c>).
    /// </summary>
    /// <param name="text">The message to show.</param>
    /// <param name="window">The message window, 0 for the usual one.</param>
    /// <param name="x">Where the window goes on the screen, horizontally.</param>
    /// <param name="y">Where the window goes, vertically.</param>
    /// <param name="anchor">Which point of the window <paramref name="x"/> and <paramref name="y"/> place.</param>
    /// <param name="style">The window's style.</param>
    /// <param name="flags">The window's flags.</param>
    /// <param name="mode">How the window opens.</param>
    /// <example><code>Dialogue.Say(Text[71]);
    /// Dialogue.Say(Text.New("Nice throw!"), window: 1, y: 224);</code></example>
    [Inline(Waits = true)]
    public static void Say(TextId text, int window = 0, int x = 256, int y = 448, int anchor = 4, int style = 1, int flags = 12290, int mode = 0) {
        Open(text, window, x, y, anchor, style, flags, mode);
        WaitUntilReady(window);
        Common.MsgWaitEnd(window);
        Common.MsgClose(window);
        Common.MsgWaitExEnd(window, 1);
    }

    /// <summary>
    ///     A voiced line the player can cut short: opens the window with <paramref name="text"/>
    ///     (<see cref="Open"/>), waits until it is open (<see cref="WaitUntilOpen"/>), plays the voice prepared
    ///     with <c>Common.VoiceStandbyEnd</c> (<c>VoiceStartEnd</c>) and waits while it plays; once the window is
    ///     ready, a press of button bit 32 (<c>PadPressed() &amp; 32</c>) stops the voice (<c>VoiceStop</c>).
    ///     Then waits for the sound (<c>SoundWaitPoll</c>), closes the window and waits until it is closed.
    /// </summary>
    /// <param name="text">The message to show.</param>
    /// <param name="window">The message window, 0 for the usual one.</param>
    /// <param name="x">Where the window goes on the screen, horizontally.</param>
    /// <param name="y">Where the window goes, vertically.</param>
    /// <param name="anchor">Which point of the window <paramref name="x"/> and <paramref name="y"/> place.</param>
    /// <param name="style">The window's style.</param>
    /// <param name="flags">The window's flags.</param>
    /// <param name="mode">How the window opens.</param>
    /// <example><code>Common.VoiceStandbyEnd(0x85355cb);
    /// Common.WaitFramesUnlessHddMode(5);
    /// Dialogue.SayVoiced(Text[0]);</code></example>
    [Inline(Waits = true)]
    public static void SayVoiced(TextId text, int window = 0, int x = 256, int y = 448, int anchor = 4, int style = 1, int flags = 8194, int mode = 0) {
        Open(text, window, x, y, anchor, style, flags, mode);
        WaitUntilOpen(window);
        Common.VoiceStartEnd();
        while (Common.VoiceIsPlaying()) {
            if (Common.MsgIsReady(window) == 1 & AsBool(Common.PadPressed() & 32) & Common.AlwaysTrue019c()) {
                Common.VoiceStop();
                break;
            }
            Common.WaitFrames(1);
        }
        Common.SoundWaitPoll();
        Common.MsgClose(window);
        Common.MsgWaitExEnd(window, 1);
    }

    /// <summary>
    ///     A voiced line that plays to its end: as <see cref="SayVoiced"/>, but without the button test, so the
    ///     player cannot stop the voice.
    /// </summary>
    /// <param name="text">The message to show.</param>
    /// <param name="window">The message window, 0 for the usual one.</param>
    /// <param name="x">Where the window goes on the screen, horizontally.</param>
    /// <param name="y">Where the window goes, vertically.</param>
    /// <param name="anchor">Which point of the window <paramref name="x"/> and <paramref name="y"/> place.</param>
    /// <param name="style">The window's style.</param>
    /// <param name="flags">The window's flags.</param>
    /// <param name="mode">How the window opens.</param>
    [Inline(Waits = true)]
    public static void SayVoicedToEnd(TextId text, int window = 0, int x = 256, int y = 448, int anchor = 4, int style = 1, int flags = 8194, int mode = 0) {
        Open(text, window, x, y, anchor, style, flags, mode);
        WaitUntilOpen(window);
        Common.VoiceStartEnd();
        Common.SoundWaitPoll();
        Common.MsgClose(window);
        Common.MsgWaitExEnd(window, 1);
    }

    // An int used as a condition, as Worker.AsBool: no code.
    private static bool AsBool(int value) => throw Runtime.NotRunnable();

    /// <summary>
    ///     A voiced line, its voice prepared first, as the game does: <c>VoiceStandbyEnd(voice)</c>,
    ///     <c>WaitFramesUnlessHddMode(5)</c>, then <see cref="SayVoiced"/>.
    /// </summary>
    /// <param name="voice">The voice to prepare and play (<c>VoiceStandbyEnd</c>'s value).</param>
    /// <param name="text">The message to show.</param>
    /// <param name="window">The message window, 0 for the usual one.</param>
    /// <param name="x">Where the window goes on the screen, horizontally.</param>
    /// <param name="y">Where the window goes, vertically.</param>
    /// <param name="anchor">Which point of the window <paramref name="x"/> and <paramref name="y"/> place.</param>
    /// <param name="style">The window's style.</param>
    /// <param name="flags">The window's flags (8194 for most voiced lines).</param>
    /// <param name="mode">How the window opens.</param>
    [Inline(Waits = true)]
    public static void SayWithVoice(int voice, TextId text, int window = 0, int x = 256, int y = 448, int anchor = 4, int style = 1, int flags = 8194, int mode = 0) {
        Common.VoiceStandbyEnd(voice);
        Common.WaitFramesUnlessHddMode(5);
        SayVoiced(text, window, x, y, anchor, style, flags, mode);
    }
}
