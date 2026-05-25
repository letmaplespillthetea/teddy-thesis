Shader "Custom/TexturedMesh"
{
    Properties
    {
        _Color ("Main Color", Color) = (1,1,1,1)
        _MainTex ("Main Texture", 2D) = "white" {}
        _UseShading ("Use Shading", Float) = 1.0
        _Smoothness ("Smoothness", Range(0, 1)) = 0.8
        _SpecIntensity ("Specular Intensity", Range(0, 1)) = 0.5
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdbase nolightmap nodirlightmap nodynlightmap novertexlight
            #pragma target 3.0
            #include "UnityCG.cginc"
            #include "AutoLight.cginc"
            #include "Lighting.cginc"

            fixed4 _Color;
            sampler2D _MainTex;
            float4 _MainTex_ST;
            float _UseShading;
            float _Smoothness;
            float _SpecIntensity;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float3 normal : TEXCOORD1;
                float3 worldNormal : TEXCOORD2;
                float3 worldPos : TEXCOORD3;
                float3 viewDir : TEXCOORD4;
                SHADOW_COORDS(5)
                float4 vertex : SV_POSITION;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.normal = normalize(mul(v.normal, (float3x3)unity_ObjectToWorld));
                o.worldNormal = mul(v.normal, (float3x3)unity_ObjectToWorld);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.viewDir = normalize(_WorldSpaceCameraPos - o.worldPos);
                TRANSFER_SHADOW(o);
                
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 texColor = tex2D(_MainTex, i.uv) * _Color;
                
                if (_UseShading > 0.0)
                {
                    // Normalize the normal
                    float3 normal = normalize(i.worldNormal);
                    
                    // Get light direction from main light
                    float3 lightDir = normalize(_WorldSpaceLightPos0.xyz);
                    
                    // Calculate diffuse (Half-Lambert for cartoon/vibrant style, avoids dark shadows)
                    float diffuseFactor = dot(normal, lightDir) * 0.5 + 0.5;
                    float shadow = SHADOW_ATTENUATION(i);
                    
                    // Combine diffuse with shadow
                    float3 diffuse = diffuseFactor * _LightColor0.rgb * shadow;
                    
                    // Add ambient light with a minimum floor to keep it bright and clear
                    float3 ambient = max(ShadeSH9(float4(normal, 1)), float3(0.7, 0.7, 0.7));
                    
                    // Calculate specular (Blinn-Phong)
                    float3 halfDir = normalize(lightDir + i.viewDir);
                    float specFactor = pow(max(dot(normal, halfDir), 0.0), (1.0 - _Smoothness) * 100.0);
                    float3 specular = specFactor * _LightColor0.rgb * _SpecIntensity * shadow;
                    
                    // Final lighting
                    float3 lighting = diffuse + ambient + specular;
                    
                    return texColor * float4(lighting, 1.0);
                }
                else
                {
                    return texColor;
                }
            }
            ENDCG
        }
    }
    
    FallBack "Diffuse"
}
