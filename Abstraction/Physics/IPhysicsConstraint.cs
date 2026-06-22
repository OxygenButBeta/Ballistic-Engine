
namespace BallisticEngine;

public interface IPhysicsConstraint {
    bool IsValid { get; }
    object UserData { get; set; }
}
