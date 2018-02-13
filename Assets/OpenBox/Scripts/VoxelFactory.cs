﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;

using OpenBox;
using LiteBox.LMath;

public class VoxelFactory {
    public enum ColliderType {
        None,
        Exact,
        HalfScale,
        ThirdScale,
        QuarterScale
    }

    public enum VoxelMaterial {
        Opaque,
        Transparent
        //Emissive
    }

    struct Quad {
        public Vector3 position;
        public Color32 color;
        public Vector2 uv;
    }

    static bool IsTransparent(VoxelSet<Vec4b> voxels, Vec3i idx) {
        return voxels[idx].w > 0 && voxels[idx].w < 255;
    }

    // Adds all faces for the given index of the given voxels to the list of quads.
    static void AddFaces(VoxelSet<Vec4b> voxels, List<Quad> quads, Vec3i idx) {
        Vec3i[] normals = {
            new Vec3i(1, 0, 0),
            new Vec3i(-1, 0, 0),

            new Vec3i(0, 1, 0),
            new Vec3i(0, -1, 0),

            new Vec3i(0, 0, 1),
            new Vec3i(0, 0, -1)
        };

        bool transparent = IsTransparent(voxels, idx);

        for (int i = 0; i < normals.Length; ++i) {
            Vec3i normal = normals[i];
            Vec3i neighbor = idx + normal;
            if (voxels.IsValid(neighbor) && (voxels[neighbor].w > 0)) {
                if (transparent && IsTransparent(voxels, neighbor)) {
                    continue;
                }

                if (!transparent && voxels[neighbor].w == 255) {
                    continue;
                }
            }

            var c = voxels[idx];
            Quad q = new Quad();

            q.color = new Color32(c.x, c.y, c.z, c.w);

            Vec3i pos = idx;

            if (Vec3i.Dot(normal, new Vec3i(1)) > 0) {
                pos += normal;
            }

            q.position = new Vector3(pos.x, pos.y, pos.z);
            q.uv = new Vector2(i, 0);

            quads.Add(q);

            if (transparent) {
                // Add back facing as well for transparent quads
                q.uv = new Vector2((i - i % 2) + (i + 1) % 2, 0);
                quads.Add(q);
            }
        }
    }

    // Adds geometry for the given quads to the mesh and returns the number of submeshes.
    static int AddMeshGeometry(List<Quad>[] quads, Mesh mesh) {
        int geometryCount = 0;
        int subMeshCount = 0;

        foreach (var q in quads) {
            geometryCount += q.Count;

            if (q.Count > 0) {
                subMeshCount++;
            }
        }

        // Flatten into mesh
        Vector3[] points = new Vector3[geometryCount];
        Color32[] colors = new Color32[geometryCount];
        Vector2[] uvs = new Vector2[geometryCount];

        int idx = 0;
        foreach (var quadList in quads) {
            foreach (var quad in quadList) {
                points[idx] = quad.position;
                colors[idx] = quad.color;
                uvs[idx] = quad.uv;
                idx++;
            }
        }

        mesh.vertices = points;
        mesh.colors32 = colors;
        mesh.uv = uvs;

        return subMeshCount;
    }

    static void AddMeshIndices(List<Quad> quads, Mesh mesh, int baseIdx, int submeshIdx) {
        int[] indices = new int[quads.Count];

        int idx = 0;
        foreach (var quad in quads) {
            indices[idx] = idx + baseIdx;
            idx++;
        }

        mesh.SetIndices(indices, MeshTopology.Points, submeshIdx);
    }

    // Makes a mesh from the given voxel set.
    public static void MakeMesh(VoxelSet<Vec4b> voxels, out Mesh mesh, out Material[] materials) {
        List<Quad> quads = new List<Quad>();
        List<Quad> transparentQuads = new List<Quad>();

        // Find all visible faces
        for (int z = 0; z < voxels.Size.z; ++z) {
            for (int y = 0; y < voxels.Size.y; ++y) {
                for (int x = 0; x < voxels.Size.x; ++x) {
                    if (voxels[x, y, z].w <= 0) {
                        // Empty; skip
                        continue;
                    }

                    if (voxels[x, y, z].w < 255) {
                        AddFaces(voxels, transparentQuads, new Vec3i(x, y, z));
                    } else {
                        AddFaces(voxels, quads, new Vec3i(x, y, z));
                    }
                }
            }
        }

        mesh = new Mesh();
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

        int subMeshCount = AddMeshGeometry(new List<Quad>[] {
            quads,
            transparentQuads
        }, mesh);
        mesh.subMeshCount = subMeshCount;

        materials = new Material[subMeshCount];

        int submeshIdx = 0;
        int nextPointIdx = 0;

        // Opaque quads
        if (quads.Count > 0) {
            AddMeshIndices(quads, mesh, nextPointIdx, submeshIdx);
            materials[submeshIdx] = new Material(Shader.Find("Voxel/PointQuads"));

            nextPointIdx += quads.Count;
            submeshIdx++;
        }

        // Transparent quads
        if (quads.Count > 0) {
            AddMeshIndices(transparentQuads, mesh, nextPointIdx, submeshIdx);
            materials[submeshIdx] = new Material(Shader.Find("Voxel/PointQuadsTransparent"));

            nextPointIdx += quads.Count;
            submeshIdx++;
        }
    }

    // Makes a set of covers to completely cover the given shape.
    static List<BoxMaker.Box> MakeBoxes(VoxelSet<bool> shape, int scale) {
        List<BoxMaker.Box> boxes = BoxMaker.MakeBoxes(shape);

        if (scale != 1) {
            foreach (var box in boxes) {
                box.extents *= scale;
                box.origin *= scale;
            }
        }

        return boxes;
    }

    // Reduces the size of the given shape by the given factor.
    static VoxelSet<bool> ReduceShape(VoxelSet<bool> shape, int factor) {
        Vec3i size = Vec3i.Max(shape.Size / factor, new Vec3i(1));

        Debug.Log("Old size: " + shape.Size + "     New size: " + size);

        VoxelSet<bool> reducedShape = new VoxelSet<bool>(size);
        shape.Apply((v, idx) => {
            Vec3i targetIdx = Vec3i.Min(size - 1, idx / factor);
            reducedShape[targetIdx] = reducedShape[targetIdx] || v;
        });

        return reducedShape;
    }

    // Adds colliders to the given game object.
    public static void AddColliders(GameObject obj, VoxelSet<Vec4b> voxels, ColliderType colliderType) {
        if (colliderType != ColliderType.None) {
            VoxelSet<bool> shape = voxels.Project(v => v.w > 0);
            int scale = 1;
            if (colliderType == ColliderType.HalfScale) {
                //shape = ReduceShape(shape);
                shape = ReduceShape(shape, 2);
                scale = 2;
            }

            if (colliderType == ColliderType.ThirdScale) {
                shape = ReduceShape(shape, 3);
                scale = 3;
            }

            if (colliderType == ColliderType.QuarterScale) {
                //shape = ReduceShape(ReduceShape(shape));
                shape = ReduceShape(shape, 4);
                scale = 4;
            }

            List<BoxMaker.Box> boxes = MakeBoxes(shape, scale);

            // Add box colliders
            foreach (var boxDesc in boxes) {
                BoxCollider box = obj.AddComponent<BoxCollider>();
                box.size = new Vector3(boxDesc.extents.x, boxDesc.extents.y, boxDesc.extents.z);
                box.center = new Vector3(boxDesc.origin.x, boxDesc.origin.y, boxDesc.origin.z) + box.size / 2.0f;
            }
        }
    }

    public static GameObject Load(VoxelSet<Vec4b> voxels, ColliderType colliderType) {
        GameObject obj = new GameObject("VoxelModel");

        Material[] materials;
        Mesh mesh;
        MakeMesh(voxels, out mesh, out materials);

        MeshRenderer renderer = obj.AddComponent<MeshRenderer>();
        renderer.materials = materials;

        MeshFilter meshFilter = obj.AddComponent<MeshFilter>();
        meshFilter.mesh = mesh;

        AddColliders(obj, voxels, colliderType);

        return obj;
    }
}