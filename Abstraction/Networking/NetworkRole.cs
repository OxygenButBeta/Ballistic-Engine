namespace BallisticEngine.Networking;

// The two ORTHOGONAL authority axes (plan L3 / §4d). Never collapsed into one flag — that
// collapse is the root cause of the "IsOwner on host" / "who runs this code" edge-case class.
//
//   StateAuthority  — this machine owns the object's truth and writes its [Networked] state.
//                     The server has it for server-owned objects; a host has it for everything.
//   InputAuthority  — this machine drives the object's input (the owning client / the local player).
//
// Derived roles (computed, never stored as a third flag — see the §4d.1 truth-table):
//   IsOwner         ≡ this connection == the object's Owner          (input authority's usual partner)
//   IsProxy         ≡ !StateAuthority && !InputAuthority             (NEITHER — false on a host always)
//   AutonomousProxy ≡ !StateAuthority &&  InputAuthority             (owning client: predicts + reads input)
//   SimulatedProxy  ≡  IsProxy                                       (interpolated, neither authority)
[Flags]
public enum NetworkAuthority {
    None = 0,
    State = 1 << 0,
    Input = 1 << 1,
    Both = State | Input,
}

// Process topology (plan §4d) — about the PROCESS, not any object. The only bare server/client
// boolean, mirrored by the Network static facade. Distinct from object readiness (IsSpawned).
public enum NetworkTopology {
    // Not networked at all — pure offline (no Network.StartHost called). The engine's default
    // before any gameplay-framework wiring runs, so a plain scene behaves exactly as today.
    Offline,
    Server,   // dedicated server: authority, no local player
    Client,   // pure client: connected to a remote server
    Host,     // listen-server: server + a local client in one process (single-player uses this)
}
