# Ballistic Engine

Ballistic Engine is a hobby project written in **C#**, using [OpenTK](https://opentk.net/) for rendering and [Assimp.Net](https://github.com/assimp/assimp-net) for asset importing.  
It is still under active development and serves as a learning and experimental ground for building a lightweight and flexible game engine.

---

## ✨ Features

- **Shared Asset Management** – efficient resource handling across the engine.
- **Entity-Component System (ECS)** – entities can have components added or removed dynamically.
- **Built-in Components**:
  - Static Mesh Renderer
  - Free Look Camera & Camera System
- **Modern Rendering Pipeline**:
  - `Diffuse`, `Normal`, `Metallic`, `Roughness`, `AO` textures
  - `Skybox` with reflections
  - Diffuse lighting
  - Instanced drawing
- **Composition System** – clean separation of entities and their behaviors.
- **Graphics Abstraction**:
  - OpenGL code is fully isolated in a dedicated `Graphics` class
  - `GPUBuffer` base class handles VRAM allocation

---

## 🖼️ Screenshots & Videos

Screenshots (16:9):

<img src="https://github.com/user-attachments/assets/b0a6f0ff-ceb6-424b-92cb-0abdaebef505" width="600" />
<img src="https://github.com/user-attachments/assets/ee589419-5371-4b64-a08b-a24ffeb6cb74" width="600" />

![Screenshot 3](https://github.com/user-attachments/assets/816fb1af-331c-4cb8-96e8-f26593459c94)

🎥 [Watch on YouTube](https://www.youtube.com/watch?v=6uzjT07534k)

---

## 📌 Current State

- No active editor yet (planned for future versions).
- Core architecture and rendering features are functional.

---

## ⚠️ Disclaimer

This is a **hobby project**, not intended for production use.  
The goal is to explore graphics programming, engine architecture, and game development concepts.
