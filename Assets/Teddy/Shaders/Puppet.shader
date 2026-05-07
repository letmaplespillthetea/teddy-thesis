// Upgrade NOTE: replaced 'mul(UNITY_MATRIX_MVP,*)' with 'UnityObjectToClipPos(*)'

Shader "Teddy/Demo/Puppet" {

	Properties {
		[MainColor] [PerRendererData] _Color ("Color", Color) = (1, 1, 1, 1)
		[MainTexture] [PerRendererData] _MainTex ("Main Texture", 2D) = "white" {}
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
			float3 screenPos : TANGENT;
			float2 uv : TEXCOORD0;
		};

		half4 _Color;
		sampler2D _MainTex;
		float4 _MainTex_ST;
		sampler2D _Ramp;
		half4 _ToonParams;
		half4 _DisplacementParams;

		v2f vert (appdata v) {
			v2f OUT;

			v.vertex.xyz += v.normal * snoise(v.vertex.xyz * _DisplacementParams.x + float3(0, _Time.y, 0) * _DisplacementParams.y) * _DisplacementParams.z;
			OUT.vertex = UnityObjectToClipPos(v.vertex);
			OUT.screenPos = ComputeScreenPos(OUT.vertex);
			OUT.uv = TRANSFORM_TEX(v.uv, _MainTex);

			return OUT;
		}

		ENDCG

		Pass {
			Cull Off
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			half4 frag (v2f IN) : SV_Target {
				half4 texColor = tex2D(_MainTex, IN.uv);
				return texColor * _Color;
			}
			ENDCG
		}

	}
}