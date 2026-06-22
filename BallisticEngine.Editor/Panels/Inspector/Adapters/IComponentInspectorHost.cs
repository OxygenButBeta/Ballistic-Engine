using System.Reflection;
using SysVec2 = System.Numerics.Vector2;
using SysVec3 = System.Numerics.Vector3;

namespace BallisticEngine.Editor.Inspector;

public interface IComponentInspectorHost {
    void RowWithTooltip(string label, string tooltip);
    void DrawMixedMarker(MemberInfo member, object target, object value);
    bool AxisVec3(string id, string label, ref SysVec3 v, float speed);
    bool TrackUndo(string label, bool changed);
    void MarkViewportDirty();

    void DrawAssetSlot(IProperty property);

    void DrawSceneObjectSlot(IProperty property);

    void DrawCollectionSlot(IProperty property);

    void DrawDictionarySlot(IProperty property);

    void DrawPolymorphicSlot(IProperty property, Type declaredType);

    void DrawNestedSlot(IProperty property, Type declaredType);
}
