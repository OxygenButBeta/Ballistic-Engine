namespace BallisticEngine.UI;

// A control that needs a callback AFTER layout solves (when ResolvedRect of itself + children is known).
// ScrollView uses it to clamp scroll + size the thumb; any control that positions sub-parts from solved
// sizes implements it. The UIDocument walks the tree after each solve and invokes these.
public interface IPostLayout
{
    void OnAfterLayout();
}
