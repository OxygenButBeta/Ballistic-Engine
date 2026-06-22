using System.Reflection;

namespace BallisticEngine;

public readonly record struct ComponentEntry(string DisplayName, string Menu, Type Type);
