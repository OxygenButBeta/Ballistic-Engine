using System.Reflection;

public static class SingleServiceInstaller {
    public static void InstallAllInAssembly(Assembly asm) {
        foreach (Type type in
                 asm.GetTypes()
                     .Where(x => x.GetCustomAttribute<EngineServiceAttribute>()! != null)
                     .OrderBy(type => type.GetCustomAttribute<EngineServiceAttribute>()!.Priority)) {
            
            if (type.GetConstructor(Type.EmptyTypes) == null)
                throw new InvalidOperationException($"{type.FullName} must have a parameterless constructor.");

            var instance = Activator.CreateInstance(type);

            Type genericType = typeof(Service<>);
            Type argumentType = type;
            Type closedGenericType = genericType.MakeGenericType(argumentType);
            MethodInfo setMethod = closedGenericType.GetMethod(
                "Set",
                BindingFlags.Public | BindingFlags.Static,
                null,
                [type],
                null
            );

            if (setMethod == null)
                throw new InvalidOperationException($"Service<{type.Name}> does not have a Set method.");
            setMethod.Invoke(null, [instance]);
        }
    }
}