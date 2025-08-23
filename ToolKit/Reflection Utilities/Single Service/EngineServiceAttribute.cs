
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public class EngineServiceAttribute : Attribute{
    public bool RegisterService { get; }
    public int Priority { get; }
    public EngineServiceAttribute(bool registerService = true,int priority = 0) {
        RegisterService = registerService;
        Priority = priority;
    }
}
