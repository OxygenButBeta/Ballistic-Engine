using Facebook.Yoga;
using static Facebook.Yoga.YGNodeAPI;
using static Facebook.Yoga.YGNodeStyleAPI;
using static Facebook.Yoga.YGNodeLayoutAPI;

namespace BallisticEngine.UI;

public sealed class LayoutNode
{
    readonly Node _node;

    public LayoutNode()
    {
        _node = new Node(WebConfig);
    }

    static Config _webConfig;
    static Config WebConfig
    {
        get
        {
            if (_webConfig != null) return _webConfig;
            _webConfig = new Config();
            _webConfig.SetUseWebDefaults(true);
            return _webConfig;
        }
    }

    public void InsertChild(LayoutNode child, int index) => YGNodeInsertChild(_node, child._node, (nuint)index);
    public void RemoveChild(LayoutNode child) => YGNodeRemoveChild(_node, child._node);
    public void RemoveAllChildren() => YGNodeRemoveAllChildren(_node);
    public int ChildCount => (int)YGNodeGetChildCount(_node);

    public void MarkDirty() => YGNodeMarkDirty(_node);
    public bool IsDirty => YGNodeIsDirty(_node);

    public void SetMeasure(Func<float, BallisticEngine.UI.MeasureMode, float, BallisticEngine.UI.MeasureMode, (float w, float h)> measure)
    {
        if (measure == null) { YGNodeSetMeasureFunc(_node, null); return; }
        YGNodeSetMeasureFunc(_node, (n, availW, wMode, availH, hMode) =>
        {
            var (w, h) = measure(availW, FromYoga(wMode), availH, FromYoga(hMode));
            return new YGSize { Width = w, Height = h };
        });
    }

    static BallisticEngine.UI.MeasureMode FromYoga(Facebook.Yoga.MeasureMode m) => m switch
    {
        Facebook.Yoga.MeasureMode.Exactly => BallisticEngine.UI.MeasureMode.Exactly,
        Facebook.Yoga.MeasureMode.AtMost => BallisticEngine.UI.MeasureMode.AtMost,
        _ => BallisticEngine.UI.MeasureMode.Undefined,
    };

    public void MarkDirtyIfMeasured() { if (IsMeasureSet) YGNodeMarkDirty(_node); }
    bool IsMeasureSet => YGNodeHasMeasureFunc(_node);

    public LayoutDirection Direction { get; set; } = LayoutDirection.LTR;

    public void CalculateLayout(float availableWidth, float availableHeight) =>
        YGNodeCalculateLayout(_node, availableWidth, availableHeight,
            Direction == LayoutDirection.RTL ? YGDirection.RTL : YGDirection.LTR);

    public float LayoutLeft => YGNodeLayoutGetLeft(_node);
    public float LayoutTop => YGNodeLayoutGetTop(_node);
    public float LayoutWidth => YGNodeLayoutGetWidth(_node);
    public float LayoutHeight => YGNodeLayoutGetHeight(_node);
    public bool HasNewLayout => YGNodeGetHasNewLayout(_node);
    public void ClearNewLayoutFlag() => YGNodeSetHasNewLayout(_node, false);

    public FlexDirection FlexDirection { set => YGNodeStyleSetFlexDirection(_node, ToYoga(value)); }
    public FlexWrap FlexWrap { set => YGNodeStyleSetFlexWrap(_node, ToYoga(value)); }
    public Justify JustifyContent { set => YGNodeStyleSetJustifyContent(_node, ToYoga(value)); }
    public Align AlignItems { set => YGNodeStyleSetAlignItems(_node, ToYoga(value)); }
    public Align AlignContent { set => YGNodeStyleSetAlignContent(_node, ToYoga(value)); }
    public Align AlignSelf { set => YGNodeStyleSetAlignSelf(_node, ToYoga(value)); }

    public float FlexGrow { set => YGNodeStyleSetFlexGrow(_node, value); }
    public float FlexShrink { set => YGNodeStyleSetFlexShrink(_node, value); }
    public void SetFlexBasisPoints(float points) => YGNodeStyleSetFlexBasis(_node, points);
    public void SetFlexBasisPercent(float percent) => YGNodeStyleSetFlexBasisPercent(_node, percent);
    public void SetFlexBasisAuto() => YGNodeStyleSetFlexBasisAuto(_node);

    public PositionType PositionType { set => YGNodeStyleSetPositionType(_node, ToYoga(value)); }
    public DisplayStyle Display { set => YGNodeStyleSetDisplay(_node, value == DisplayStyle.None ? YGDisplay.None : YGDisplay.Flex); }
    public Overflow Overflow { set => YGNodeStyleSetOverflow(_node, ToYoga(value)); }

    public void SetWidthPoints(float p) => YGNodeStyleSetWidth(_node, p);
    public void SetWidthPercent(float p) => YGNodeStyleSetWidthPercent(_node, p);
    public void SetWidthAuto() => YGNodeStyleSetWidthAuto(_node);
    public void SetHeightPoints(float p) => YGNodeStyleSetHeight(_node, p);
    public void SetHeightPercent(float p) => YGNodeStyleSetHeightPercent(_node, p);
    public void SetHeightAuto() => YGNodeStyleSetHeightAuto(_node);

    public void SetMinWidthPoints(float p) => YGNodeStyleSetMinWidth(_node, p);
    public void SetMinHeightPoints(float p) => YGNodeStyleSetMinHeight(_node, p);
    public void SetMaxWidthPoints(float p) => YGNodeStyleSetMaxWidth(_node, p);
    public void SetMaxHeightPoints(float p) => YGNodeStyleSetMaxHeight(_node, p);

    public void SetMarginPoints(Edge e, float p) => YGNodeStyleSetMargin(_node, ToYoga(e), p);
    public void SetPaddingPoints(Edge e, float p) => YGNodeStyleSetPadding(_node, ToYoga(e), p);
    public void SetBorderPoints(Edge e, float p) => YGNodeStyleSetBorder(_node, ToYoga(e), p);
    public void SetPositionPoints(Edge e, float p) => YGNodeStyleSetPosition(_node, ToYoga(e), p);
    public void SetPositionPercent(Edge e, float p) => YGNodeStyleSetPositionPercent(_node, ToYoga(e), p);

    public float AspectRatio { set => YGNodeStyleSetAspectRatio(_node, value); }

    public void SetGap(Gutter gutter, float points) => YGNodeStyleSetGap(_node, ToYoga(gutter), points);

    static YGGutter ToYoga(Gutter g) => g switch
    {
        Gutter.Row => YGGutter.Row,
        Gutter.Column => YGGutter.Column,
        _ => YGGutter.All,
    };

    static YGFlexDirection ToYoga(FlexDirection v) => v switch
    {
        FlexDirection.Row => YGFlexDirection.Row,
        FlexDirection.RowReverse => YGFlexDirection.RowReverse,
        FlexDirection.Column => YGFlexDirection.Column,
        FlexDirection.ColumnReverse => YGFlexDirection.ColumnReverse,
        _ => YGFlexDirection.Row,
    };

    static YGWrap ToYoga(FlexWrap v) => v switch
    {
        FlexWrap.NoWrap => YGWrap.NoWrap,
        FlexWrap.Wrap => YGWrap.Wrap,
        FlexWrap.WrapReverse => YGWrap.WrapReverse,
        _ => YGWrap.NoWrap,
    };

    static YGAlign ToYoga(Align v) => v switch
    {
        Align.Auto => YGAlign.Auto,
        Align.FlexStart => YGAlign.FlexStart,
        Align.Center => YGAlign.Center,
        Align.FlexEnd => YGAlign.FlexEnd,
        Align.Stretch => YGAlign.Stretch,
        Align.Baseline => YGAlign.Baseline,
        Align.SpaceBetween => YGAlign.SpaceBetween,
        Align.SpaceAround => YGAlign.SpaceAround,
        Align.SpaceEvenly => YGAlign.SpaceEvenly,
        _ => YGAlign.Stretch,
    };

    static YGJustify ToYoga(Justify v) => v switch
    {
        Justify.FlexStart => YGJustify.FlexStart,
        Justify.Center => YGJustify.Center,
        Justify.FlexEnd => YGJustify.FlexEnd,
        Justify.SpaceBetween => YGJustify.SpaceBetween,
        Justify.SpaceAround => YGJustify.SpaceAround,
        Justify.SpaceEvenly => YGJustify.SpaceEvenly,
        _ => YGJustify.FlexStart,
    };

    static YGPositionType ToYoga(PositionType v) => v switch
    {
        PositionType.Relative => YGPositionType.Relative,
        PositionType.Absolute => YGPositionType.Absolute,
        PositionType.Static => YGPositionType.Static,
        _ => YGPositionType.Relative,
    };

    static YGOverflow ToYoga(Overflow v) => v switch
    {
        Overflow.Visible => YGOverflow.Visible,
        Overflow.Hidden => YGOverflow.Hidden,
        Overflow.Scroll => YGOverflow.Scroll,
        _ => YGOverflow.Visible,
    };

    static YGEdge ToYoga(Edge e) => e switch
    {
        Edge.Left => YGEdge.Left,
        Edge.Top => YGEdge.Top,
        Edge.Right => YGEdge.Right,
        Edge.Bottom => YGEdge.Bottom,
        Edge.Start => YGEdge.Start,
        Edge.End => YGEdge.End,
        Edge.Horizontal => YGEdge.Horizontal,
        Edge.Vertical => YGEdge.Vertical,
        Edge.All => YGEdge.All,
        _ => YGEdge.All,
    };
}
