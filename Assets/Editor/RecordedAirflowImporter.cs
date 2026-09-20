using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor.AssetImporters;
using UnityEngine;

[ScriptedImporter(1, "npz")]
public class RecordedAirflowImporter : ScriptedImporter
{
    public override void OnImportAsset(AssetImportContext ctx)
    {
        using (var zip = ZipFile.OpenRead(ctx.assetPath))
        {
            int[] ps, vs, ts;
            float[] p = Read(zip, "coordinates_m.npy", out ps);
            float[] v = Read(zip, "velocity_ms.npy", out vs);
            float[] t = Read(zip, "times_s.npy", out ts);
            if (ps.Length != 2 || ps[1] != 3 || ps[0] < 4 || ts.Length != 1 || t.Length < 1 ||
                vs.Length != 3 || vs[0] != t.Length || vs[1] != ps[0] || vs[2] != 3)
                throw new InvalidDataException("Expected coordinates [N,3], velocity [T,N,3], times [T].");
            for (int i = 1; i < t.Length; i++)
                if (t[i] <= t[i-1]) throw new InvalidDataException("Times must increase.");
            var asset = ScriptableObject.CreateInstance<RecordedAirflow>();
            asset.positions = Vectors(p); asset.velocities = Vectors(v); asset.times = t;
            asset.bounds = new Bounds(asset.positions[0], Vector3.zero);
            foreach (var point in asset.positions) asset.bounds.Encapsulate(point);
            foreach (var velocity in asset.velocities) asset.maxSpeed = Mathf.Max(asset.maxSpeed, velocity.magnitude);
            ctx.AddObjectToAsset("flow", asset); ctx.SetMainObject(asset);
        }
    }
    static Vector3[] Vectors(float[] values)
    {
        var result = new Vector3[values.Length / 3];
        for (int i = 0; i < result.Length; i++) result[i] = new Vector3(values[i*3], values[i*3+2], values[i*3+1]);
        return result;
    }
    static float[] Read(ZipArchive zip, string name, out int[] shape)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidDataException("Missing " + name);
        using (var reader = new BinaryReader(entry.Open()))
        {
            byte[] magic = reader.ReadBytes(6);
            if (magic.Length != 6 || magic[0] != 147 || Encoding.ASCII.GetString(magic,1,5) != "NUMPY")
                throw new InvalidDataException("Invalid NPY magic.");
            int major = reader.ReadByte(); reader.ReadByte();
            if (major < 1 || major > 3) throw new InvalidDataException("Unsupported NPY version.");
            int length = major == 1 ? reader.ReadUInt16() : checked((int)reader.ReadUInt32());
            string header = Encoding.UTF8.GetString(reader.ReadBytes(length));
            if (!Regex.IsMatch(header, "'descr':\\s*'<f4'") || !Regex.IsMatch(header, "'fortran_order':\\s*False"))
                throw new InvalidDataException("Expected C-order little-endian float32: " + name);
            var matches = Regex.Matches(Regex.Match(header, "'shape':\\s*\\(([^)]*)\\)").Groups[1].Value, "\\d+");
            shape = new int[matches.Count]; int count = 1;
            for (int i = 0; i < shape.Length; i++) { shape[i] = int.Parse(matches[i].Value); count = checked(count * shape[i]); }
            var data = new float[count];
            for (int i = 0; i < count; i++)
            {
                data[i] = reader.ReadSingle();
                if (float.IsNaN(data[i]) || float.IsInfinity(data[i])) throw new InvalidDataException("Non-finite sample.");
            }
            return data;
        }
    }
}
