using UnityEngine;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System;

namespace mattatz.TeddySystem.Example {
    public static class GLBExporter {
        public static void ExportAnimation(string path, Mesh mesh, Texture2D tex, List<Vector3[]> frames, float frameRate) {
            using (var binStream = new MemoryStream())
            using (var binWriter = new BinaryWriter(binStream)) {
                
                int bufferOffset = 0;
                List<string> accessors = new List<string>();
                List<string> bufferViews = new List<string>();

                Action<byte[], int, string, int, string, string> AddBufferView = (data, count, type, componentType, min, max) => {
                    int padding = (4 - (data.Length % 4)) % 4;
                    binWriter.Write(data);
                    for(int i=0; i<padding; i++) binWriter.Write((byte)0);
                    
                    int byteLength = data.Length;
                    bufferViews.Add($"{{\"buffer\":0,\"byteOffset\":{bufferOffset},\"byteLength\":{byteLength}}}");
                    
                    string minMax = "";
                    if (min != null) minMax = $",\"min\":{min},\"max\":{max}";
                    
                    accessors.Add($"{{\"bufferView\":{bufferViews.Count-1},\"byteOffset\":0,\"componentType\":{componentType},\"count\":{count},\"type\":\"{type}\"{minMax}}}");
                    
                    bufferOffset += byteLength + padding;
                };

                var verts = mesh.vertices;
                byte[] vData = new byte[verts.Length * 12];
                Vector3 minV = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                Vector3 maxV = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                for(int i=0; i<verts.Length; i++) {
                    var v = verts[i];
                    v.x = -v.x; 
                    Buffer.BlockCopy(BitConverter.GetBytes(v.x), 0, vData, i*12, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes(v.y), 0, vData, i*12+4, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes(v.z), 0, vData, i*12+8, 4);
                    minV = Vector3.Min(minV, v);
                    maxV = Vector3.Max(maxV, v);
                }
                AddBufferView(vData, verts.Length, "VEC3", 5126, $"[{minV.x.ToString("G", System.Globalization.CultureInfo.InvariantCulture)},{minV.y.ToString("G", System.Globalization.CultureInfo.InvariantCulture)},{minV.z.ToString("G", System.Globalization.CultureInfo.InvariantCulture)}]", $"[{maxV.x.ToString("G", System.Globalization.CultureInfo.InvariantCulture)},{maxV.y.ToString("G", System.Globalization.CultureInfo.InvariantCulture)},{maxV.z.ToString("G", System.Globalization.CultureInfo.InvariantCulture)}]");

                var norms = mesh.normals;
                if (norms.Length == 0) norms = new Vector3[verts.Length];
                byte[] nData = new byte[norms.Length * 12];
                for(int i=0; i<norms.Length; i++) {
                    var n = norms[i];
                    n.x = -n.x;
                    Buffer.BlockCopy(BitConverter.GetBytes(n.x), 0, nData, i*12, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes(n.y), 0, nData, i*12+4, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes(n.z), 0, nData, i*12+8, 4);
                }
                AddBufferView(nData, norms.Length, "VEC3", 5126, null, null);

                var uvs = mesh.uv;
                byte[] uData = new byte[uvs.Length * 8];
                for(int i=0; i<uvs.Length; i++) {
                    Buffer.BlockCopy(BitConverter.GetBytes(uvs[i].x), 0, uData, i*8, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes(1f - uvs[i].y), 0, uData, i*8+4, 4);
                }
                AddBufferView(uData, uvs.Length, "VEC2", 5126, null, null);

                var tris = mesh.triangles;
                byte[] iData = new byte[tris.Length * 4];
                for(int i=0; i<tris.Length; i+=3) {
                    Buffer.BlockCopy(BitConverter.GetBytes(tris[i]), 0, iData, i*4, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes(tris[i+2]), 0, iData, (i+1)*4, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes(tris[i+1]), 0, iData, (i+2)*4, 4);
                }
                AddBufferView(iData, tris.Length, "SCALAR", 5125, null, null);

                int targetOffset = accessors.Count;
                List<string> targets = new List<string>();
                if (frames != null && frames.Count > 0) {
                    for(int f=0; f<frames.Count; f++) {
                        var fVerts = frames[f];
                        byte[] mData = new byte[verts.Length * 12];
                        Vector3 minM = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                        Vector3 maxM = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                        for(int i=0; i<verts.Length; i++) {
                            var v = fVerts[i] - verts[i];
                            v.x = -v.x;
                            Buffer.BlockCopy(BitConverter.GetBytes(v.x), 0, mData, i*12, 4);
                            Buffer.BlockCopy(BitConverter.GetBytes(v.y), 0, mData, i*12+4, 4);
                            Buffer.BlockCopy(BitConverter.GetBytes(v.z), 0, mData, i*12+8, 4);
                            minM = Vector3.Min(minM, v);
                            maxM = Vector3.Max(maxM, v);
                        }
                        AddBufferView(mData, verts.Length, "VEC3", 5126, $"[{minM.x.ToString("G", System.Globalization.CultureInfo.InvariantCulture)},{minM.y.ToString("G", System.Globalization.CultureInfo.InvariantCulture)},{minM.z.ToString("G", System.Globalization.CultureInfo.InvariantCulture)}]", $"[{maxM.x.ToString("G", System.Globalization.CultureInfo.InvariantCulture)},{maxM.y.ToString("G", System.Globalization.CultureInfo.InvariantCulture)},{maxM.z.ToString("G", System.Globalization.CultureInfo.InvariantCulture)}]");
                        targets.Add($"{{\"POSITION\":{targetOffset + f}}}");
                    }

                    int timeAccessor = accessors.Count;
                    byte[] tData = new byte[frames.Count * 4];
                    float maxT = 0;
                    for(int f=0; f<frames.Count; f++) {
                        float t = f / frameRate;
                        maxT = t;
                        Buffer.BlockCopy(BitConverter.GetBytes(t), 0, tData, f*4, 4);
                    }
                    AddBufferView(tData, frames.Count, "SCALAR", 5126, "[0]", $"[{maxT.ToString("G", System.Globalization.CultureInfo.InvariantCulture)}]");

                    int weightAccessor = accessors.Count;
                    byte[] wData = new byte[frames.Count * frames.Count * 4];
                    for(int f=0; f<frames.Count; f++) {
                        for(int t=0; t<frames.Count; t++) {
                            float w = (f == t) ? 1f : 0f;
                            Buffer.BlockCopy(BitConverter.GetBytes(w), 0, wData, (f * frames.Count + t) * 4, 4);
                        }
                    }
                    AddBufferView(wData, frames.Count * frames.Count, "SCALAR", 5126, null, null);
                }

                int imgAccessor = -1;
                if (tex != null) {
                    byte[] png = tex.EncodeToPNG();
                    if (png != null) {
                        int padding = (4 - (png.Length % 4)) % 4;
                        binWriter.Write(png);
                        for(int i=0; i<padding; i++) binWriter.Write((byte)0);
                        bufferViews.Add($"{{\"buffer\":0,\"byteOffset\":{bufferOffset},\"byteLength\":{png.Length}}}");
                        imgAccessor = bufferViews.Count - 1;
                        bufferOffset += png.Length + padding;
                    }
                }

                int totalBufferLength = bufferOffset;

                StringBuilder json = new StringBuilder();
                json.Append("{\"asset\":{\"version\":\"2.0\"},");
                json.Append("\"scene\":0,\"scenes\":[{\"nodes\":[0]}],");
                json.Append("\"nodes\":[{\"mesh\":0}],");

                json.Append("\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0,\"NORMAL\":1,\"TEXCOORD_0\":2},\"indices\":3,\"material\":0");
                if (targets.Count > 0) json.Append(",\"targets\":[{" + string.Join("},{", targets).Replace("{{\"POSITION\"", "{\"POSITION\"").Replace("}}}", "}") + "}]");
                json.Append("}]");
                if (targets.Count > 0) {
                    json.Append(",\"weights\":[");
                    for(int i=0; i<frames.Count; i++) json.Append((i==0?"1":",0"));
                    json.Append("]");
                }
                json.Append("}],");

                if (targets.Count > 0) {
                    int timeAccessor = accessors.Count - 2;
                    int weightAccessor = accessors.Count - 1;
                    json.Append("\"animations\":[{\"channels\":[{\"sampler\":0,\"target\":{\"node\":0,\"path\":\"weights\"}}],");
                    json.Append($"\"samplers\":[{{\"input\":{timeAccessor},\"interpolation\":\"LINEAR\",\"output\":{weightAccessor}}}]}},");
                }

                if (imgAccessor != -1) {
                    json.Append("\"materials\":[{\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0},\"metallicFactor\":0.0,\"roughnessFactor\":1.0},\"doubleSided\":true}],");
                    json.Append("\"textures\":[{\"source\":0}],");
                    json.Append($"\"images\":[{{\"bufferView\":{imgAccessor},\"mimeType\":\"image/png\"}}],");
                } else {
                    json.Append("\"materials\":[{\"pbrMetallicRoughness\":{\"baseColorFactor\":[1,1,1,1],\"metallicFactor\":0.0,\"roughnessFactor\":1.0},\"doubleSided\":true}],");
                }

                json.Append("\"buffers\":[{\"byteLength\":" + totalBufferLength + "}],");
                json.Append("\"bufferViews\":[" + string.Join(",", bufferViews) + "],");
                json.Append("\"accessors\":[" + string.Join(",", accessors) + "]");
                json.Append("}");

                byte[] jsonBytes = Encoding.UTF8.GetBytes(json.ToString());
                int jsonPadding = (4 - (jsonBytes.Length % 4)) % 4;

                using (var fs = new FileStream(path, FileMode.Create))
                using (var bw = new BinaryWriter(fs)) {
                    bw.Write(0x46546C67);
                    bw.Write(2);
                    bw.Write(12 + 8 + jsonBytes.Length + jsonPadding + 8 + totalBufferLength);
                    
                    bw.Write(jsonBytes.Length + jsonPadding);
                    bw.Write(0x4E4F534A);
                    bw.Write(jsonBytes);
                    for(int i=0; i<jsonPadding; i++) bw.Write((byte)0x20);
                    
                    bw.Write(totalBufferLength);
                    bw.Write(0x004E4942);
                    bw.Write(binStream.ToArray());
                }
            }
        }
    }
}
