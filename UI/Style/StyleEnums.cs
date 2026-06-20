namespace BallisticEngine.UI;

// Engine-side style enums. These mirror CSS so a ported Claude design reads 1:1, and they keep
// the rest of the UI layer from ever importing Facebook.Yoga directly — LayoutNode is the single
// place that translates these to Yoga's enums (the IJob/IPhysicsWorld facade idiom).
//
// Defaults are chosen to match the WEB box model, NOT Unity UI Toolkit's: a fresh element lays
// its children out in a ROW (like an HTML <div> with display:flex), which removes the single most
// common port fixup ("inline text stacked vertically" — see the port skill's gotcha table).

public enum FlexDirection { Row, RowReverse, Column, ColumnReverse }

public enum FlexWrap { NoWrap, Wrap, WrapReverse }

// CSS justify-content / align-* keyword set. "Start"/"End" map to flex-start/flex-end.
public enum Align { Auto, FlexStart, Center, FlexEnd, Stretch, Baseline, SpaceBetween, SpaceAround, SpaceEvenly }

public enum Justify { FlexStart, Center, FlexEnd, SpaceBetween, SpaceAround, SpaceEvenly }

public enum PositionType { Relative, Absolute, Static }

public enum DisplayStyle { Flex, None }

public enum Overflow { Visible, Hidden, Scroll }

// Constraint mode for a measure callback's available width/height (Yoga's MeasureMode), so a measured
// leaf (text) knows whether the available size is a hard size (Exactly), an upper bound to wrap within
// (AtMost), or unconstrained (Undefined). (P4.4)
public enum MeasureMode { Undefined, Exactly, AtMost }

// CSS white-space (subset): Normal wraps at the available width; NoWrap keeps one line (honoring '\n').
// (P4.8)
public enum WhiteSpace { Normal, NoWrap }

// CSS text-overflow when content is clipped (overflow:hidden): Clip cuts; Ellipsis appends "…". (P4.8)
public enum TextOverflow { Clip, Ellipsis }

// Which edge a position/margin/padding/border value targets. Mirrors CSS edges; "Horizontal"/
// "Vertical"/"All" are convenience shorthands Yoga supports natively.
public enum Edge { Left, Top, Right, Bottom, Start, End, Horizontal, Vertical, All }

// Which axis a flex gap targets (CSS gap / row-gap / column-gap). (P4.5)
public enum Gutter { All, Row, Column }
