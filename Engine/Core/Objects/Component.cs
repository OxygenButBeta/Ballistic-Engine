namespace BallisticEngine;

public class Component : BObject {
    protected Entity entity;

    internal void AttachToEntity(Entity targetEntity) {
        entity = targetEntity;
    }

    protected override void OnInstanceCreated() {
      
    }
}