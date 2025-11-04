Shader "Universal Render Pipeline/GPUBoidShader"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (1,1,1,1)
        _BaseMap ("Base Map", 2D) = "white" {}
        _Smoothness ("Smoothness", Range(0,1)) = 0.5
        _Metallic ("Metallic", Range(0,1)) = 0.0
        _Scale ("Scale", Float) = 1.0
    }
    
    SubShader
    {
        Tags 
        { 
            "RenderType" = "Opaque" 
            "RenderPipeline" = "UniversalPipeline" 
        }
        LOD 200

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:setup

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float2 uv : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct BoidData
            {
                float3 position;
                float3 velocity;
                float3 acceleration;
                int targetIndex;
            };

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _BaseMap_ST;
                float _Smoothness;
                float _Metallic;
                float _Scale;
            CBUFFER_END

            #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
            StructuredBuffer<BoidData> boidBuffer;
            #endif

            void setup()
            {
                #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
                BoidData boid = boidBuffer[unity_InstanceID];
                
                // Calculate rotation from velocity
                float3 forward = normalize(boid.velocity);
                float3 up = float3(0, 1, 0);
                
                // Avoid gimbal lock when velocity is straight up/down
                if (abs(dot(forward, up)) > 0.99)
                {
                    up = float3(1, 0, 0);
                }
                
                float3 right = normalize(cross(up, forward));
                up = cross(forward, right);
                
                // Create transformation matrix
                float4x4 rotationMatrix = float4x4(
                    right.x, up.x, forward.x, 0,
                    right.y, up.y, forward.y, 0,
                    right.z, up.z, forward.z, 0,
                    0, 0, 0, 1
                );
                
                float4x4 scaleMatrix = float4x4(
                    _Scale, 0, 0, 0,
                    0, _Scale, 0, 0,
                    0, 0, _Scale, 0,
                    0, 0, 0, 1
                );
                
                float4x4 translationMatrix = float4x4(
                    1, 0, 0, boid.position.x,
                    0, 1, 0, boid.position.y,
                    0, 0, 1, boid.position.z,
                    0, 0, 0, 1
                );
                
                unity_ObjectToWorld = mul(translationMatrix, mul(rotationMatrix, scaleMatrix));
                unity_WorldToObject = unity_ObjectToWorld;
                unity_WorldToObject._14_24_34 *= -1;
                unity_WorldToObject._11_22_33 = 1.0f / unity_WorldToObject._11_22_33;
                #endif
            }

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS);

                output.positionHCS = vertexInput.positionCS;
                output.positionWS = vertexInput.positionWS;
                output.normalWS = normalInput.normalWS;
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);

                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                // Sample texture
                half4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
                
                // Simple lighting calculation
                Light mainLight = GetMainLight();
                float3 lightDir = normalize(mainLight.direction);
                float3 normal = normalize(input.normalWS);
                
                float NdotL = saturate(dot(normal, lightDir));
                float3 lighting = mainLight.color * NdotL;
                
                // Add ambient
                lighting += SampleSH(normal);
                
                return half4(albedo.rgb * lighting, albedo.a);
            }
            ENDHLSL
        }
        
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:setup

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"

            struct BoidData
            {
                float3 position;
                float3 velocity;
                float3 acceleration;
                int targetIndex;
            };

            CBUFFER_START(UnityPerMaterial)
                float _Scale;
            CBUFFER_END

            #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
            StructuredBuffer<BoidData> boidBuffer;
            #endif

            void setup()
            {
                #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
                BoidData boid = boidBuffer[unity_InstanceID];
                
                float3 forward = normalize(boid.velocity);
                float3 up = float3(0, 1, 0);
                
                if (abs(dot(forward, up)) > 0.99)
                {
                    up = float3(1, 0, 0);
                }
                
                float3 right = normalize(cross(up, forward));
                up = cross(forward, right);
                
                float4x4 rotationMatrix = float4x4(
                    right.x, up.x, forward.x, 0,
                    right.y, up.y, forward.y, 0,
                    right.z, up.z, forward.z, 0,
                    0, 0, 0, 1
                );
                
                float4x4 scaleMatrix = float4x4(
                    _Scale, 0, 0, 0,
                    0, _Scale, 0, 0,
                    0, 0, _Scale, 0,
                    0, 0, 0, 1
                );
                
                float4x4 translationMatrix = float4x4(
                    1, 0, 0, boid.position.x,
                    0, 1, 0, boid.position.y,
                    0, 0, 1, boid.position.z,
                    0, 0, 0, 1
                );
                
                unity_ObjectToWorld = mul(translationMatrix, mul(rotationMatrix, scaleMatrix));
                unity_WorldToObject = unity_ObjectToWorld;
                unity_WorldToObject._14_24_34 *= -1;
                unity_WorldToObject._11_22_33 = 1.0f / unity_WorldToObject._11_22_33;
                #endif
            }
            ENDHLSL
        }
    }
    
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}