
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public class EngineServiceAttribute : Attribute{
    public EngineServiceAttribute(bool registerService = true) {
        
    }
}
