// Upgrade NOTE: replaced 'mul(UNITY_MATRIX_MVP,*)' with 'UnityObjectToClipPos(*)'

Shader "Teddy/Demo/Mesh" {

	Properties {
	}

	SubShader {
		Tags { "RenderType"="Opaque" }
		LOD 100

		CGINCLUDE

		#include "UnityCG.cginc"

		#pragma target 3.0

		struct appdata {
			float4 vertex : POSITION;
			float3 normal : NORMAL;
			float2 uv : TEXCOORD0;
		};

		struct v2f {
			float4 vertex : SV_POSITION;
			float3 normal : NORMAL;
			float2 uv : TEXCOORD0;
		};

		v2f vert (appdata v) {
			v2f o;
			o.vertex = UnityObjectToClipPos(v.vertex);
			o.normal = mul(unity_ObjectToWorld, float4(v.normal, 0)).xyz;
			o.uv = v.uv;
			return o;
		}

		fixed3 normal_color (fixed3 norm) {
			return (normalize(norm) + 1.0) * 0.5;
		}

		ENDCG

		Pass {
			Cull Off
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			half4 frag (v2f IN) : SV_Target {
				float3 dx = ddx(IN.screenPos.xyz);
				float3 dy = ddy(IN.screenPos.xyz);
				float3 normal = normalize(cross(dx, dy));

				half d = dot(normal, normalize(float3(0.5, -0.75, 0.5))) * _ToonParams.x + _ToonParams.y;
				d = saturate(d);
				half3 ramp = tex2D(_Ramp, float2(d, d)).rgb;
				ramp = pow(ramp, _ToonParams.z);
				return half4(ramp, 1) * _Color;
			}
			ENDCG
		}

	}
}
