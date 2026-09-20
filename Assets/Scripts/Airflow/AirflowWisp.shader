Shader "Airflow/Wisp"
{
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct Input { float4 vertex : POSITION; float4 color : COLOR; };
            struct Output { float4 vertex : SV_POSITION; float4 color : COLOR; };
            Output vert(Input v) { Output o; o.vertex = UnityObjectToClipPos(v.vertex); o.color = v.color; return o; }
            float4 frag(Output i) : SV_Target { return i.color; }
            ENDHLSL
        }
    }
}
