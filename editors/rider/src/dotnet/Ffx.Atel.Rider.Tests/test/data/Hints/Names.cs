// event/obj/aa/aaaa0100/aaaa0100.ebp, decompiled by atel.
// Made-up script and names.
namespace Events.Aa.Aaaa0100;

public sealed class W02
{
    void Battle()
    {
        Btl.UseCommand(Target.Aaaa, Command.Bbbb);
        Btl.UseCommand(Ffx.Target.Aaaa, (Command)0xa000);
        Btl.UseCommand((Target)4098, (Ffx.Command)12288);
        Btl.UseCommand(Target.Own, Command.Zzzz); // no names: no hints
        Btl.UseCommand(x.Target.Aaaa, (Model)0x3000); // not the type, not a named type
        Common.MapJump(Map.Cccc0100, 0); // no maps table exported: no hint
    }
}
