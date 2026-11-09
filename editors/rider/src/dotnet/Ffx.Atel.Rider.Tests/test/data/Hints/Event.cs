// event/obj/aa/aaaa0100/aaaa0100.ebp, decompiled by atel.
// Made-up script and text.
namespace Events.Aa.Aaaa0100;

public sealed class W01
{
    void Talk()
    {
        Common.MsgSetText(0, Text[1]);
        Common.MsgSetText(1, Text[0]); // Text[1] in a comment: no hint for it
        Common.MsgSetText(2, Text[2]); // a message without text: no hint
        Common.MsgSetText(3, Text[9]); // no message 9: no hint
        Common.MsgSetText(4, Other[1]);
        Common.MsgSetText(5, this.Text[3]);
        Common.MsgSetText(6, Text[i]);
        Common.MsgSetText(7, Text[4]); // an empty first line: no speaker
    }
}
