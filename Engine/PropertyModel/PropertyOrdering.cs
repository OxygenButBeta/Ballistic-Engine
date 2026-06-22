using System.Reflection;

namespace BallisticEngine;

public static class PropertyOrdering {
    public static int OrderOf(MemberInfo member) =>
        member.GetCustomAttribute<PropertyOrderAttribute>()?.Order ?? 0;

    public static T[] Sort<T>(IEnumerable<T> items, Func<T, int> orderOf) {
        if (items is null) throw new ArgumentNullException(nameof(items));
        if (orderOf is null) throw new ArgumentNullException(nameof(orderOf));

        var indexed = items.Select((item, index) => (item, order: orderOf(item), index)).ToList();
        return indexed
            .OrderBy(e => e.order)
            .ThenBy(e => e.index)
            .Select(e => e.item)
            .ToArray();
    }

    public static MemberInfo[] Sort(IEnumerable<MemberInfo> members) => Sort(members, OrderOf);
}
