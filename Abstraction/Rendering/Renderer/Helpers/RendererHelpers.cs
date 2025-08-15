using BallisticEngine;
using BallisticEngine.Rendering;

public static class RendererHelpers
{
    public static List<BatchGroup<IStaticMeshRenderer>> CreateBatchGroupsForOpaqueDrawables()
    {
        List<BatchGroup<IStaticMeshRenderer>> batchGroups = [];

        IGrouping<(Mesh SharedMesh, Material SharedMaterial), IStaticMeshRenderer>[] groups = RuntimeSet<IStaticMeshRenderer>.ReadOnlyCollection
            .GroupBy(drawable => (drawable.SharedMesh, drawable.SharedMaterial)).ToArray();

        foreach (IGrouping<(Mesh SharedMesh, Material SharedMaterial), IStaticMeshRenderer> group in groups)
        {
            if (!group.Any())
                continue;

            BatchGroup<IStaticMeshRenderer> batchGroup = BatchGroupPool<IStaticMeshRenderer>.Rent();

            batchGroup.SetDrawable(group.First());

            foreach (IStaticMeshRenderer drawable in group) {
                batchGroup.Add(drawable);
                drawable.RenderedThisFrame = true;
            }

            batchGroups.Add(batchGroup);
        }

        return batchGroups;
    }
}