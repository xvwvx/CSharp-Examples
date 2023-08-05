using Arch.Core;
using Any.NetEvent;

namespace SourceGenerator.Debug;

public class Test
{

    public Test(NetEventBus eventBus)
    {
        eventBus.Register(this);
    }

    [NetEvent(1111)]
    public void Test0(in World world, ref Entity entity, ref Test request)
    {
    }

    [NetEvent(1234, 4567)]
    public int? Test1(in World world, ref Entity entity, ref int request)
    {
        return 789;
    }
}