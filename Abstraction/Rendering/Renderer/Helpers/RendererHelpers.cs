using BallisticEngine;
using BallisticEngine.Rendering;

public static class RendererHelpers
{
    public static List<BatchGroup<IOpaqueDrawable>> CreateBatchGroupsForOpaqueDrawables()
    {
        List<BatchGroup<IOpaqueDrawable>> batchGroups = [];

        IGrouping<(Mesh SharedMesh, Material SharedMaterial), IOpaqueDrawable>[] groups = RuntimeSet<IOpaqueDrawable>.ReadOnlyCollection
            .GroupBy(drawable => (drawable.SharedMesh, drawable.SharedMaterial)).ToArray();

        foreach (IGrouping<(Mesh SharedMesh, Material SharedMaterial), IOpaqueDrawable> group in groups)
        {
            if (!group.Any())
                continue;

            BatchGroup<IOpaqueDrawable> batchGroup = BatchGroupPool<IOpaqueDrawable>.Rent();

            batchGroup.SetDrawable(group.First());

            foreach (IOpaqueDrawable drawable in group) {
                batchGroup.Add(drawable);
                drawable.RenderedThisFrame = true;
            }

            batchGroups.Add(batchGroup);
        }

        return batchGroups;
    }
}