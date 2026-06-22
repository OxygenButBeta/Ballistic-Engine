namespace BallisticEngine.Editor.Inspector;

public sealed class BEventDrawer : ITypeDrawer {
    public bool CanDraw(Type t) => typeof(BEvent).IsAssignableFrom(t);
    public bool Draw(IProperty p, IInspectorGui gui) {
        BEventEditor.Draw(p.Name, p.Get() as BEvent);
        return false;
    }
}

public sealed class AnimationCurveDrawer : ITypeDrawer {
    readonly IComponentInspectorHost host;
    public AnimationCurveDrawer(IComponentInspectorHost host) => this.host = host;
    public bool CanDraw(Type t) => typeof(AnimationCurve).IsAssignableFrom(t);
    public bool Draw(IProperty p, IInspectorGui gui) {
        if (p.Get() is not AnimationCurve curve) { gui.Unsupported(p.ValueType); return false; }
        bool changed = EditorWidgets.CurveEditor(p.Name, curve, host.MarkViewportDirty);
        if (changed) host.MarkViewportDirty();
        return changed;
    }
}

public sealed class ColorGradientDrawer : ITypeDrawer {
    readonly IComponentInspectorHost host;
    public ColorGradientDrawer(IComponentInspectorHost host) => this.host = host;
    public bool CanDraw(Type t) => typeof(ColorGradient).IsAssignableFrom(t);
    public bool Draw(IProperty p, IInspectorGui gui) {
        if (p.Get() is not ColorGradient gradient) { gui.Unsupported(p.ValueType); return false; }
        bool changed = EditorWidgets.GradientEditor(p.Name, gradient);
        if (changed) host.MarkViewportDirty();
        return changed;
    }
}

public sealed class AssetSlotDrawer : ITypeDrawer {
    readonly IComponentInspectorHost host;
    public AssetSlotDrawer(IComponentInspectorHost host) => this.host = host;
    public bool CanDraw(Type t) => typeof(BObject).IsAssignableFrom(t);
    public bool Draw(IProperty p, IInspectorGui gui) {
        host.DrawAssetSlot(p);
        return false;
    }
}

public sealed class SceneObjectRefDrawer : ITypeDrawer {
    readonly IComponentInspectorHost host;
    public SceneObjectRefDrawer(IComponentInspectorHost host) => this.host = host;
    public bool CanDraw(Type t) => t == typeof(EntityRef) || t == typeof(ComponentRef);
    public bool Draw(IProperty p, IInspectorGui gui) {
        host.DrawSceneObjectSlot(p);
        return false;
    }
}

public sealed class CollectionDrawer : ITypeDrawer {
    readonly IComponentInspectorHost host;
    public CollectionDrawer(IComponentInspectorHost host) => this.host = host;
    public bool CanDraw(Type t) {
        if (t == typeof(string)) return false;
        if (t.IsArray && t.GetArrayRank() == 1) return true;
        return t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>);
    }
    public bool Draw(IProperty p, IInspectorGui gui) {
        host.DrawCollectionSlot(p);
        return false;
    }
}

public sealed class DictionaryDrawer : ITypeDrawer {
    readonly IComponentInspectorHost host;
    public DictionaryDrawer(IComponentInspectorHost host) => this.host = host;
    public bool CanDraw(Type t) =>
        t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>);
    public bool Draw(IProperty p, IInspectorGui gui) {
        host.DrawDictionarySlot(p);
        return false;
    }
}

public sealed class DictionaryValueProperty : IProperty {
    readonly Func<object> get;
    readonly Action<object> assign;

    public DictionaryValueProperty(string name, Type valueType, Func<object> get, Action<object> assign) {
        Name = name;
        ValueType = valueType;
        this.get = get;
        this.assign = assign;
    }

    public string Name { get; }
    public string Label => Name;
    public string Tooltip => null;
    public Type ValueType { get; }
    public object Owner => null;

    public object Get() => get();
    public void Set(object value) => assign(value);

    public MemberAttributes Attributes => MemberAttributes.None;
    public (float min, float max)? Range => null;
    public bool IsColor => false;
    public bool Hdr => false;
    public bool HasOverrideToggle => false;
    public bool Overridden { get => false; set { } }
    public bool TryGetSiblingValue(string memberName, out object value) { value = null; return false; }
}

public sealed class CollectionElementProperty : IProperty {
    readonly Func<object> get;
    readonly Action<object> assign;

    public CollectionElementProperty(string name, Type elementType, Func<object> get, Action<object> assign) {
        Name = name;
        ValueType = elementType;
        this.get = get;
        this.assign = assign;
    }

    public string Name { get; }
    public string Label => Name;
    public string Tooltip => null;
    public Type ValueType { get; }
    public object Owner => null;

    public object Get() => get();
    public void Set(object value) => assign(value);

    public MemberAttributes Attributes => MemberAttributes.None;
    public (float min, float max)? Range => null;
    public bool IsColor => false;
    public bool Hdr => false;
    public bool HasOverrideToggle => false;
    public bool Overridden { get => false; set { } }
    public bool TryGetSiblingValue(string memberName, out object value) { value = null; return false; }
}

public sealed class PolymorphicDrawer : ITypeDrawer {
    readonly IComponentInspectorHost host;
    public PolymorphicDrawer(IComponentInspectorHost host) => this.host = host;

    public bool CanDraw(Type t) =>
        t is { IsInterface: true } or { IsAbstract: true } && !typeof(BObject).IsAssignableFrom(t);
    public bool Draw(IProperty p, IInspectorGui gui) {
        host.DrawPolymorphicSlot(p, p.ValueType);
        return false;
    }
}

public sealed class NestedDrawer : ITypeDrawer {
    readonly IComponentInspectorHost host;
    public NestedDrawer(IComponentInspectorHost host) => this.host = host;
    public bool CanDraw(Type t) => PropertyCategories.Classify(t) == PropertyCategory.Nested;
    public bool Draw(IProperty p, IInspectorGui gui) {
        host.DrawNestedSlot(p, p.ValueType);
        return false;
    }
}
