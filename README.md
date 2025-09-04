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

Screenshots (16:9) and a demo video will be added here:

![Screenshot Placeholder](<img width="967" height="847" alt="image" src="https://github.com/user-attachments/assets/b0a6f0ff-ceb6-424b-92cb-0abdaebef505" />)
![Screenshot Placeholder](<img width="1900" height="1018" alt="image" src="https://github.com/user-attachments/assets/ee589419-5371-4b64-a08b-a24ffeb6cb74" />)

🎥 [Watch on YouTube]([https://youtube.com/your-demo-link](https://www.youtube.com/watch?v=6uzjT07534k))

---

## 📌 Current State

- No active editor yet (planned for future versions).
- Core architecture and rendering features are functional.

---

## 🚀 Getting Started

Clone the repository:

```bash
git clone https://github.com/yourusername/ballistic-engine.git
