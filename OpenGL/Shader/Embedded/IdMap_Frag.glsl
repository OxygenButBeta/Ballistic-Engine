#version 460 core

// Writes the draw's integer ID into an R32UI target (0 = background).
layout(location = 0) out uint outId;

uniform int drawId;

void main() {
    outId = uint(drawId);
}
