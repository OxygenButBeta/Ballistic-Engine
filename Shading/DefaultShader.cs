namespace BallisticEngine.Rendering;

//Just a text structure for the shaders
// TODO: Implement a shader loader
public struct DefaultShader {
    public const string VertexShader = @"#version 330 core
                layout (location = 0) in vec3 aPosition; // vertex coordinates
                layout (location = 1) in vec2 aTexCoord; // texture coordinates
				layout(location = 3) in vec4 instance_matrix_0;
				layout(location = 4) in vec4 instance_matrix_1;
				layout(location = 5) in vec4 instance_matrix_2;
				layout(location = 6) in vec4 instance_matrix_3;
				
				uniform bool isInstanced;
                out vec2 texCoord;

                // uniform variables
                uniform mat4 view;
                uniform mat4 projection;
				uniform mat4 model;

                void main() 
                {
					    mat4 modelMatrix = isInstanced
			        ?  transpose(mat4(instance_matrix_0, instance_matrix_1, instance_matrix_2, instance_matrix_3))
			        : model;

	               gl_Position = vec4(aPosition, 1.0) * modelMatrix * view * projection ; // coordinates
	                texCoord = aTexCoord;
                }";

    public const string FragmentShader = @"
				#version 330 core
				in vec2 texCoord;

                out vec4 FragColor;

                uniform sampler2D texture0;

                void main() 
                {
	           // FragColor = vec4(1f,1f,1f,1f);
					   FragColor =  texture(texture0, texCoord);
                }";
}