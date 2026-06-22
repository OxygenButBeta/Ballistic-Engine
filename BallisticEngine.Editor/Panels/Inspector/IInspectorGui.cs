using SysVec3 = System.Numerics.Vector3;

namespace BallisticEngine.Editor.Inspector;

public interface IInspectorGui {
    void PushId(string id);
    void PopId();

    void BeginDisabled();
    void EndDisabled();

    void BeginRow(IProperty property);
    void EndRow();

    void Header(string text);
    void Space(float height);
    void HelpBox(string text);

    bool Checkbox(ref bool v);
    bool SliderFloat(ref float v, float min, float max);
    bool DragFloat(ref float v, float speed);
    bool SliderInt(ref int v, int min, int max);
    bool DragInt(ref int v);
    bool InputText(ref string v, int maxLength);
    bool Combo(ref int index, string[] names);
    bool ColorEdit3(ref SysVec3 v, bool hdr);
    bool DragFloat2(ref System.Numerics.Vector2 v, float speed);
    bool DragFloat3(ref SysVec3 v, float speed);

    void Unsupported(System.Type type);
}
