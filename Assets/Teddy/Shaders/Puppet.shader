// Upgrade NOTE: replaced 'mul(UNITY_MATRIX_MVP,*)' with 'UnityObjectToClipPos(*)'

Shader "Teddy/Demo/Puppet" {

	Properties {
		_Color ("Color", Color) = (1, 1, 1, 1)
		_MainTex ("Main Texture", 2D) = "white" {}
		_Ramp ("Toon Ramp (RGB)", 2D) = "gray" {} 
		_ToonParams ("Toon Params", Vector) = (0.47, 0.32, 1.44, -1)
		_DisplacementParams ("Displacement Params", Vector) = (0.15, 0.75, 1.0, -1)
	}

	SubShader {
		Tags { "RenderType"="Opaque" }
		LOD 100

		CGINCLUDE

		#include "UnityCG.cginc"
		#include "./Common/SimplexNoise3D.cginc"

		#pragma target 3.0

		struct appdata {
			float4 vertex : POSITION;
			float3 normal : NORMAL;
			float2 uv : TEXCOORD0;
		};

		struct v2f {
			float4 vertex : SV_POSITION;
			float3 normal : TEXCOORD1;
			float2 uv : TEXCOORD0;
		};

		half4 _Color;
		sampler2D _MainTex;
		sampler2D _Ramp;
		half4 _ToonParams;
		half4 _DisplacementParams;

		v2f vert (appdata v) {
			v2f OUT;

			v.vertex.xyz += v.normal * snoise(v.vertex.xyz * _DisplacementParams.x + float3(0, _Time.y, 0) * _DisplacementParams.y) * _DisplacementParams.z;
			OUT.vertex = UnityObjectToClipPos(v.vertex);
			OUT.normal = UnityObjectToWorldNormal(v.normal);
			OUT.uv = v.uv;

			return OUT;
		}

		ENDCG

		Pass {
			Cull Off
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#include "Lighting.cginc"
			
			half4 frag (v2f IN) : SV_Target {
				float3 normal = normalize(IN.normal);
				half4 texColor = tex2D(_MainTex, IN.uv);

				// Light direction in world space
				float3 lightDir = normalize(_WorldSpaceLightPos0.xyz);

				// Toon diffuse term
				half d = dot(normal, lightDir) * _ToonParams.x + _ToonParams.y;
				d = saturate(d);
				
				half3 ramp = tex2D(_Ramp, float2(d, d)).rgb;
				ramp = pow(ramp, _ToonParams.z);
				
				// Standard ambient light estimation to prevent pure black in shadows
				half3 ambient = max(ShadeSH9(half4(normal, 1)), half3(0.4, 0.4, 0.4));
				half3 shading = ambient + ramp * _LightColor0.rgb;
				
				return texColor * half4(shading, 1) * _Color;
			}
			ENDCG
		}

	}
}