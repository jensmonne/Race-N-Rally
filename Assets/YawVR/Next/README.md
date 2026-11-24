# YawVR/Next: protocol v2 for the Rust yawvr-server

Used by `Assets/YawVR/Scripts/YawController.RustServer.cs` when **Use Rust Server** is ticked on the
YawController. With it unticked, nothing in this folder is used.

- `Core/`, `Generated/`: copied unchanged from the YawServer repository (`csharp/YawVR.Next/Runtime/Core`
  and `Runtime/Generated`). Pure C#, no UnityEngine (`noEngineReferences`). Update by copying again, do
  not edit here. Generated code lives in the namespace `YawVR.Proto` (no clash with the SDK's `YawVR`).
- `Plugins/Google.Protobuf.dll`: 3.36.1, netstandard2.0, from nuget.org (matches protoc 36.1).
- `YawVR.Next.asmdef`: its own assembly, referenced automatically by the game scripts.
- `link.xml`: keeps the assembly intact under IL2CPP stripping.

The password: set the same text in two places.
- On the Pi, in `/etc/yawvr-rust/yawvr-server.toml` under `[auth]`: `password = "your password"`
  (then `sudo systemctl restart yawvr-rust`).
- In Unity, on the YawController: **Rust Password**.
The protocol key is derived from it (SHA-256 with a fixed prefix). Simple, but a short password can be
guessed from recorded network traffic; use a long one if that matters.
