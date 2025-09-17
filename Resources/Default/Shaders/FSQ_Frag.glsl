#version 330 core

in vec2 TexCoords;     
out vec4 FragColor;    

uniform sampler2D hdrTexture; 

// ACES Tonemap
vec3 ACESFilm(vec3 x) {
    float a = 2.51;
    float b = 0.03;
    float c = 2.43;
    float d = 0.59;
    float e = 0.14;
    return clamp((x*(a*x+b)) / (x*(c*x+d)+e), 0.0, 1.0);
}

// Basit random (grain için)
float rand(vec2 co) {
    return fract(sin(dot(co, vec2(12.9898,78.233))) * 43758.5453);
}

void main()
{
    vec3 color = texture(hdrTexture, TexCoords).rgb;

    // 1) Exposure sabit + ACES tonemap
    float exposure = 1.2; // biraz daha parlak
    color = vec3(1.0) - exp(-color * exposure);
    color = ACESFilm(color);

    // 2) Kontrast & Saturation
    float contrast = 1.05;
    float saturation = 1.1;
    color = pow(color, vec3(contrast));
    float gray = dot(color, vec3(0.299, 0.587, 0.114));
    color = mix(vec3(gray), color, saturation);

    // 3) Vignette
    float dist = length(TexCoords - 0.5);
    float vignette = smoothstep(0.8, 0.5, dist);
    color *= vignette;

    // 4) Hafif sharpening (tek texture üzerinden unsharp mask benzeri)
    vec2 texel = 1.0 / textureSize(hdrTexture, 0);
    vec3 blur =
        texture(hdrTexture, TexCoords + vec2(-texel.x, 0.0)).rgb +
        texture(hdrTexture, TexCoords + vec2(texel.x, 0.0)).rgb +
        texture(hdrTexture, TexCoords + vec2(0.0, -texel.y)).rgb +
        texture(hdrTexture, TexCoords + vec2(0.0, texel.y)).rgb;
    blur *= 0.25;
    color = mix(blur, color, 1.2); // 1.2 = sharpen gücü

    // 5) Film Grain
    float grain = rand(TexCoords * 1280.0);
    color += (grain - 0.5) * 0.015;

    // 6) Gamma düzeltme
    color = pow(color, vec3(1.0/2.2));

    FragColor = vec4(color, 1.0);
}
