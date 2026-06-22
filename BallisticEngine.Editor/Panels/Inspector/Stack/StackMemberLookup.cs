using System.Reflection;

namespace BallisticEngine.Editor.Inspector;

internal static class StackMemberLookup {
    public static MemberInfo MemberOf(IProperty property) => property switch {
        MemberProperty mp => mp.Member,
        VolumeParamProperty vp => vp.Field,
        _ => null,
    };
}
