using System;
using UnityEngine;

namespace Unity.USDToolkit
{
    /// <summary>
    /// Skins a mesh with dual quaternions, as UsdSkel's <c>dualQuaternion</c> skinning method does,
    /// for the meshes that ask for it (Unity's SkinnedMeshRenderer only blends linearly). The
    /// importer adds one, in place of a SkinnedMeshRenderer, to each such mesh: the object keeps its
    /// MeshFilter and MeshRenderer, the Mesh keeps its bone weights, bind poses and blend shapes,
    /// and this component poses it from <see cref="Bones"/> every frame the bones or weights change.
    /// </summary>
    /// <remarks>
    /// The deformation follows UsdSkel point for point: each joint's skinning transform is split
    /// into its rotation and translation, blended as dual quaternions on the hemisphere of the
    /// vertex's heaviest joint, and the rest (scale and shear) blended linearly first; blend shapes
    /// apply before skinning. It runs on the CPU, in the Editor as in players, and draws through
    /// <see cref="MeshRenderer.additionalVertexStreams"/>, so the Mesh asset and the scene are never
    /// written to: what is saved is the bind pose, and the pose is computed again when the object
    /// loads. That costs a pass over the vertices whenever the pose changes -- fine for characters,
    /// worth knowing for crowds, where turning <see cref="UsdImportOptions.DualQuaternionSkinning"/>
    /// off gives GPU linear skinning instead.
    /// </remarks>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    [AddComponentMenu("USD/USD Dual Quaternion Skin")]
    public sealed class UsdDualQuaternionSkin : MonoBehaviour
    {
        [SerializeField] private Transform[] bones = Array.Empty<Transform>();
        [Tooltip("The skeleton's object: skinning happens in its space.")]
        [SerializeField] private Transform rootBone;
        [Tooltip("The mesh's bind-time transform into the skeleton's space (UsdSkel geomBindTransform).")]
        [SerializeField] private Matrix4x4 geomBindTransform = Matrix4x4.identity;
        [SerializeField] private float[] blendShapeWeights = Array.Empty<float>();

        // Caches, all [NonSerialized]: a domain reload keeps the private fields Unity can serialize
        // and drops the rest (Matrix3[], the jagged arrays), and a half-kept cache throws in Compute.
        // Dropped together, they are read again from the mesh. stream, a Mesh, survives on its own.

        // What was read from the mesh, kept until it changes.
        [NonSerialized] private Mesh source;
        [NonSerialized] private Vector3[] restPositions;
        [NonSerialized] private Vector3[] restNormals;
        [NonSerialized] private Vector4[] restTangents;
        [NonSerialized] private Matrix4x4[] bindposes;
        [NonSerialized] private int[] influenceStarts;
        [NonSerialized] private int[] influenceJoints;
        [NonSerialized] private float[] influenceWeights;
        [NonSerialized] private int[] pivots;
        [NonSerialized] private Vector3[][][] shapeDeltas;
        [NonSerialized] private Vector3[][][] shapeNormalDeltas;
        [NonSerialized] private float[][] shapeFrameWeights;

        // Per frame.
        [NonSerialized] private Vector3[] positions;
        [NonSerialized] private Vector3[] normals;
        [NonSerialized] private Vector4[] tangents;
        [NonSerialized] private Vector3[] blendedPositions;
        [NonSerialized] private Vector3[] blendedNormals;
        [NonSerialized] private Quaternion[] jointReal;
        [NonSerialized] private Quaternion[] jointDual;
        [NonSerialized] private Matrix3[] jointScale;
        [NonSerialized] private Matrix3[] jointScaleNormal;
        [NonSerialized] private Matrix4x4[] lastBones;
        [NonSerialized] private Matrix4x4 lastFrame;
        [NonSerialized] private float[] lastWeights;
        private Mesh stream;

        /// <summary>The joints the mesh's bone weights index, in the skeleton's order.</summary>
        public Transform[] Bones
        {
            get => (Transform[])bones.Clone();
            set
            {
                bones = value ?? Array.Empty<Transform>();
                lastBones = null;
            }
        }

        /// <summary>The skeleton's object; skinning is computed in its space, as UsdSkel's is.</summary>
        public Transform RootBone
        {
            get => rootBone;
            set
            {
                rootBone = value;
                lastBones = null;
            }
        }

        /// <summary>The mesh's bind-time transform into the skeleton's space (geomBindTransform).</summary>
        public Matrix4x4 GeomBindTransform
        {
            get => geomBindTransform;
            set
            {
                geomBindTransform = value;
                lastBones = null;
            }
        }

        /// <summary>The bind-pose mesh: what the MeshFilter holds.</summary>
        public Mesh SharedMesh => TryGetComponent(out MeshFilter filter) ? filter.sharedMesh : null;

        /// <summary>A blend shape's weight, in Unity's 0-100 range like SkinnedMeshRenderer's.</summary>
        public float GetBlendShapeWeight(int index) =>
            index >= 0 && index < blendShapeWeights.Length ? blendShapeWeights[index] : 0f;

        /// <summary>Sets a blend shape's weight, in Unity's 0-100 range like SkinnedMeshRenderer's.</summary>
        public void SetBlendShapeWeight(int index, float weight)
        {
            Mesh mesh = SharedMesh;
            int count = mesh != null ? mesh.blendShapeCount : 0;
            if (index < 0 || index >= count)
            {
                return;
            }

            if (blendShapeWeights.Length < count)
            {
                Array.Resize(ref blendShapeWeights, count);
            }

            blendShapeWeights[index] = weight;
        }

        /// <summary>Poses the mesh now, whether or not anything changed since the last frame.</summary>
        public void Deform()
        {
            lastBones = null;
            DeformIfChanged();
        }

        /// <summary>
        /// Writes the posed mesh into <paramref name="mesh"/>: this object's mesh with the deformed
        /// vertices, normals and tangents, in this object's space, as SkinnedMeshRenderer.BakeMesh
        /// with useScale gives them.
        /// </summary>
        public void BakeMesh(Mesh mesh)
        {
            if (mesh == null)
            {
                throw new ArgumentNullException(nameof(mesh));
            }

            Mesh shared = SharedMesh;
            if (shared == null || !Prepare(shared))
            {
                return;
            }

            Compute();
            mesh.Clear();
            mesh.indexFormat = shared.indexFormat;
            mesh.SetVertices(positions);
            mesh.subMeshCount = shared.subMeshCount;
            for (int s = 0; s < shared.subMeshCount; s++)
            {
                mesh.SetTriangles(shared.GetTriangles(s), s, false);
            }

            mesh.SetNormals(normals);
            if (tangents != null)
            {
                mesh.SetTangents(tangents);
            }

            var uv = new System.Collections.Generic.List<Vector4>();
            for (int channel = 0; channel < 8; channel++)
            {
                shared.GetUVs(channel, uv);
                if (uv.Count == positions.Length)
                {
                    mesh.SetUVs(channel, uv);
                }
            }

            mesh.RecalculateBounds();
        }

        internal void Initialize(Transform[] joints, Transform skeleton, Matrix4x4 geomBind, float[] weights)
        {
            bones = joints ?? Array.Empty<Transform>();
            rootBone = skeleton;
            geomBindTransform = geomBind;
            blendShapeWeights = weights ?? Array.Empty<float>();
            lastBones = null;
        }

        private void OnEnable()
        {
            lastBones = null;
            DeformIfChanged();
        }

        private void LateUpdate()
        {
            DeformIfChanged();
        }

        private void OnDisable()
        {
            if (TryGetComponent(out MeshRenderer meshRenderer))
            {
                if (meshRenderer.additionalVertexStreams == stream)
                {
                    meshRenderer.additionalVertexStreams = null;
                }

                meshRenderer.ResetLocalBounds();
            }

            lastBones = null;
        }

        private void OnDestroy()
        {
            if (stream != null)
            {
                if (Application.isPlaying)
                {
                    Destroy(stream);
                }
                else
                {
                    DestroyImmediate(stream);
                }
            }
        }

        private void OnValidate()
        {
            lastBones = null;
            source = null;
        }

        private void DeformIfChanged()
        {
            Mesh shared = SharedMesh;
            // A renderer that is off draws nothing to pose (a point-instancer prototype is drawn
            // from BakeMesh instead).
            if (shared == null || rootBone == null || !TryGetComponent(out MeshRenderer meshRenderer) ||
                !meshRenderer.enabled || !Prepare(shared))
            {
                return;
            }

            Matrix4x4 frame = transform.worldToLocalMatrix * rootBone.localToWorldMatrix;
            bool changed = lastBones == null || lastBones.Length != bones.Length || frame != lastFrame ||
                           lastWeights == null || lastWeights.Length != blendShapeWeights.Length;
            if (lastBones == null || lastBones.Length != bones.Length)
            {
                lastBones = new Matrix4x4[bones.Length];
            }

            for (int j = 0; j < bones.Length; j++)
            {
                Matrix4x4 current = bones[j] != null ? bones[j].localToWorldMatrix : Matrix4x4.identity;
                changed |= current != lastBones[j];
                lastBones[j] = current;
            }

            if (lastWeights == null || lastWeights.Length != blendShapeWeights.Length)
            {
                lastWeights = new float[blendShapeWeights.Length];
            }

            for (int s = 0; s < blendShapeWeights.Length; s++)
            {
                changed |= blendShapeWeights[s] != lastWeights[s];
                lastWeights[s] = blendShapeWeights[s];
            }

            lastFrame = frame;
            if (!changed && meshRenderer.additionalVertexStreams == stream && stream != null)
            {
                return;
            }

            Compute();
            if (stream == null)
            {
                stream = new Mesh { name = shared.name + " (dual quaternion)", hideFlags = HideFlags.DontSave };
                stream.MarkDynamic();
            }

            stream.SetVertices(positions);
            stream.SetNormals(normals);
            if (tangents != null)
            {
                stream.SetTangents(tangents);
            }

            meshRenderer.additionalVertexStreams = stream;
            meshRenderer.localBounds = BoundsOf(positions);
        }

        // Reads what skinning needs from the mesh, once per mesh.
        private bool Prepare(Mesh mesh)
        {
            if (mesh == source && restPositions != null)
            {
                return true;
            }

            source = null;
            if (!mesh.isReadable)
            {
                return false;
            }

            restPositions = mesh.vertices;
            restNormals = mesh.normals;
            if (restNormals.Length != restPositions.Length)
            {
                restNormals = new Vector3[restPositions.Length];
            }

            restTangents = mesh.tangents;
            if (restTangents.Length != restPositions.Length)
            {
                restTangents = null;
            }

            bindposes = mesh.bindposes;
            var counts = mesh.GetBonesPerVertex();
            var weights = mesh.GetAllBoneWeights();
            int vertexCount = restPositions.Length;
            influenceStarts = new int[vertexCount + 1];
            influenceJoints = new int[weights.Length];
            influenceWeights = new float[weights.Length];
            pivots = new int[vertexCount];
            int next = 0;
            for (int v = 0; v < vertexCount; v++)
            {
                influenceStarts[v] = next;
                int count = v < counts.Length ? counts[v] : 0;
                float heaviest = -1f;
                pivots[v] = -1;
                for (int i = 0; i < count; i++, next++)
                {
                    influenceJoints[next] = weights[next].boneIndex;
                    influenceWeights[next] = weights[next].weight;
                    if (weights[next].weight > heaviest)
                    {
                        heaviest = weights[next].weight;
                        pivots[v] = weights[next].boneIndex;
                    }
                }
            }

            influenceStarts[vertexCount] = next;

            int shapes = mesh.blendShapeCount;
            var tangentDeltas = new Vector3[vertexCount];
            shapeDeltas = new Vector3[shapes][][];
            shapeNormalDeltas = new Vector3[shapes][][];
            shapeFrameWeights = new float[shapes][];
            for (int s = 0; s < shapes; s++)
            {
                int frames = mesh.GetBlendShapeFrameCount(s);
                shapeDeltas[s] = new Vector3[frames][];
                shapeNormalDeltas[s] = new Vector3[frames][];
                shapeFrameWeights[s] = new float[frames];
                for (int f = 0; f < frames; f++)
                {
                    shapeDeltas[s][f] = new Vector3[vertexCount];
                    shapeNormalDeltas[s][f] = new Vector3[vertexCount];
                    mesh.GetBlendShapeFrameVertices(s, f, shapeDeltas[s][f], shapeNormalDeltas[s][f], tangentDeltas);
                    shapeFrameWeights[s][f] = mesh.GetBlendShapeFrameWeight(s, f);
                }
            }

            if (blendShapeWeights.Length < shapes)
            {
                Array.Resize(ref blendShapeWeights, shapes);
            }

            positions = new Vector3[vertexCount];
            normals = new Vector3[vertexCount];
            tangents = restTangents != null ? new Vector4[vertexCount] : null;
            blendedPositions = new Vector3[vertexCount];
            blendedNormals = new Vector3[vertexCount];
            source = mesh;
            lastBones = null;
            return true;
        }

        private void Compute()
        {
            int vertexCount = restPositions.Length;
            Array.Copy(restPositions, blendedPositions, vertexCount);
            Array.Copy(restNormals, blendedNormals, vertexCount);
            ApplyBlendShapes();

            // Skeleton space throughout, as UsdSkel skins: points go in through geomBindTransform,
            // and come out of skeleton space into this object's, where the renderer draws them.
            Matrix4x4 skeletonToWorld = rootBone != null ? rootBone.localToWorldMatrix : transform.localToWorldMatrix;
            Matrix4x4 worldToSkeleton = skeletonToWorld.inverse;
            Matrix4x4 geomBind = geomBindTransform;
            Matrix4x4 bindInverse = geomBind.inverse;
            Matrix4x4 output = transform.worldToLocalMatrix * skeletonToWorld;
            Matrix3 geomBindNormal = Matrix3.Of(geomBind).InverseTranspose();
            Matrix3 outputNormal = Matrix3.Of(output).InverseTranspose();

            Matrix3 geomBind3 = Matrix3.Of(geomBind);
            Matrix3 output3 = Matrix3.Of(output);
            int joints = bindposes.Length;
            if (jointReal == null || jointReal.Length != joints)
            {
                jointReal = new Quaternion[joints];
                jointDual = new Quaternion[joints];
                jointScale = new Matrix3[joints];
                jointScaleNormal = new Matrix3[joints];
            }

            Quaternion[] real = jointReal;
            Quaternion[] dual = jointDual;
            Matrix3[] scale = jointScale;
            Matrix3[] scaleNormal = jointScaleNormal;
            bool scaled = false;
            for (int j = 0; j < joints; j++)
            {
                Transform bone = j < bones.Length ? bones[j] : null;
                // bindposes[j] is inverse(bind_j) * geomBind, so this is the joint's UsdSkel
                // skinning transform, jointSkel_j * inverse(bind_j), in skeleton space.
                Matrix4x4 skinning = (bone != null ? worldToSkeleton * bone.localToWorldMatrix : Matrix4x4.identity) *
                                     bindposes[j] * bindInverse;
                Matrix3 linear = Matrix3.Of(skinning);
                Matrix3 rotation = linear.PolarRotation();
                real[j] = rotation.ToQuaternion();
                var translation = new Quaternion(skinning.m03, skinning.m13, skinning.m23, 0f);
                dual[j] = Scale(translation * real[j], 0.5f);
                scale[j] = rotation.Transpose() * linear;
                scaleNormal[j] = scale[j].InverseTranspose();
                scaled |= !scale[j].IsIdentity(1e-6f);
            }

            for (int v = 0; v < vertexCount; v++)
            {
                Vector3 point = geomBind.MultiplyPoint3x4(blendedPositions[v]);
                Vector3 normal = geomBindNormal * blendedNormals[v];
                int start = influenceStarts[v];
                int end = influenceStarts[v + 1];
                int pivot = pivots[v];
                Quaternion pivotRotation = pivot >= 0 && pivot < joints ? real[pivot] : new Quaternion(0f, 0f, 0f, 0f);
                var sumReal = new Quaternion(0f, 0f, 0f, 0f);
                var sumDual = new Quaternion(0f, 0f, 0f, 0f);
                Vector3 scaledPoint = Vector3.zero;
                Vector3 scaledNormal = Vector3.zero;
                for (int i = start; i < end; i++)
                {
                    int j = influenceJoints[i];
                    float w = influenceWeights[i];
                    if (j < 0 || j >= joints || w == 0f)
                    {
                        continue;
                    }

                    if (scaled)
                    {
                        scaledPoint += (scale[j] * point) * w;
                        scaledNormal += (scaleNormal[j] * normal) * w;
                    }

                    if (Quaternion.Dot(real[j], pivotRotation) < 0f)
                    {
                        w = -w;
                    }

                    sumReal = Add(sumReal, Scale(real[j], w));
                    sumDual = Add(sumDual, Scale(dual[j], w));
                }

                if (!scaled)
                {
                    scaledPoint = point;
                    scaledNormal = normal;
                }

                // Normalized as GfDualQuat does: a unit real part and a dual part orthogonal to it.
                float length = Mathf.Sqrt(Quaternion.Dot(sumReal, sumReal));
                if (length < 1e-8f)
                {
                    sumReal = Quaternion.identity;
                    sumDual = new Quaternion(0f, 0f, 0f, 0f);
                }
                else
                {
                    sumReal = Scale(sumReal, 1f / length);
                    sumDual = Scale(sumDual, 1f / length);
                    sumDual = Add(sumDual, Scale(sumReal, -Quaternion.Dot(sumReal, sumDual)));
                }

                var r = new Vector3(sumReal.x, sumReal.y, sumReal.z);
                var d = new Vector3(sumDual.x, sumDual.y, sumDual.z);
                Vector3 translation = 2f * (sumReal.w * d - sumDual.w * r + Vector3.Cross(r, d));
                Vector3 skinned = sumReal * scaledPoint + translation;
                positions[v] = output.MultiplyPoint3x4(skinned);
                normals[v] = (outputNormal * (sumReal * scaledNormal)).normalized;
                if (tangents != null)
                {
                    Vector4 rest = restTangents[v];
                    Vector3 direction = (output3 * (sumReal * (geomBind3 * new Vector3(rest.x, rest.y, rest.z)))).normalized;
                    tangents[v] = new Vector4(direction.x, direction.y, direction.z, rest.w);
                }
            }
        }

        // Unity's blend-shape interpolation: linear from zero to the first frame, between frames,
        // and on past the last frame along its final segment.
        private void ApplyBlendShapes()
        {
            for (int s = 0; s < shapeDeltas.Length; s++)
            {
                float weight = s < blendShapeWeights.Length ? blendShapeWeights[s] : 0f;
                float[] frameWeights = shapeFrameWeights[s];
                int frames = frameWeights.Length;
                if (weight == 0f || frames == 0)
                {
                    continue;
                }

                int a;
                int b;
                float t;
                if (frames == 1 || weight <= frameWeights[0])
                {
                    a = -1;
                    b = 0;
                    t = frameWeights[0] != 0f ? weight / frameWeights[0] : 0f;
                }
                else
                {
                    b = 1;
                    while (b < frames - 1 && weight > frameWeights[b])
                    {
                        b++;
                    }

                    a = b - 1;
                    float span = frameWeights[b] - frameWeights[a];
                    t = span != 0f ? (weight - frameWeights[a]) / span : 1f;
                }

                Vector3[] to = shapeDeltas[s][b];
                Vector3[] toNormal = shapeNormalDeltas[s][b];
                Vector3[] from = a >= 0 ? shapeDeltas[s][a] : null;
                Vector3[] fromNormal = a >= 0 ? shapeNormalDeltas[s][a] : null;
                for (int v = 0; v < blendedPositions.Length; v++)
                {
                    Vector3 start = from != null ? from[v] : Vector3.zero;
                    Vector3 startNormal = fromNormal != null ? fromNormal[v] : Vector3.zero;
                    blendedPositions[v] += start + (to[v] - start) * t;
                    blendedNormals[v] += startNormal + (toNormal[v] - startNormal) * t;
                }
            }
        }

        private static Bounds BoundsOf(Vector3[] points)
        {
            if (points.Length == 0)
            {
                return new Bounds();
            }

            var bounds = new Bounds(points[0], Vector3.zero);
            for (int i = 1; i < points.Length; i++)
            {
                bounds.Encapsulate(points[i]);
            }

            return bounds;
        }

        private static Quaternion Scale(Quaternion q, float s) => new Quaternion(q.x * s, q.y * s, q.z * s, q.w * s);

        private static Quaternion Add(Quaternion a, Quaternion b) => new Quaternion(a.x + b.x, a.y + b.y, a.z + b.z, a.w + b.w);

        // A 3x3 matrix, column-vector convention like Matrix4x4's upper-left block.
        private struct Matrix3
        {
            public float M00, M01, M02, M10, M11, M12, M20, M21, M22;

            public static Matrix3 Of(Matrix4x4 m) => new Matrix3
            {
                M00 = m.m00, M01 = m.m01, M02 = m.m02,
                M10 = m.m10, M11 = m.m11, M12 = m.m12,
                M20 = m.m20, M21 = m.m21, M22 = m.m22
            };

            public static Vector3 operator *(Matrix3 m, Vector3 v) => new Vector3(
                m.M00 * v.x + m.M01 * v.y + m.M02 * v.z,
                m.M10 * v.x + m.M11 * v.y + m.M12 * v.z,
                m.M20 * v.x + m.M21 * v.y + m.M22 * v.z);

            public static Matrix3 operator *(Matrix3 a, Matrix3 b) => new Matrix3
            {
                M00 = a.M00 * b.M00 + a.M01 * b.M10 + a.M02 * b.M20,
                M01 = a.M00 * b.M01 + a.M01 * b.M11 + a.M02 * b.M21,
                M02 = a.M00 * b.M02 + a.M01 * b.M12 + a.M02 * b.M22,
                M10 = a.M10 * b.M00 + a.M11 * b.M10 + a.M12 * b.M20,
                M11 = a.M10 * b.M01 + a.M11 * b.M11 + a.M12 * b.M21,
                M12 = a.M10 * b.M02 + a.M11 * b.M12 + a.M12 * b.M22,
                M20 = a.M20 * b.M00 + a.M21 * b.M10 + a.M22 * b.M20,
                M21 = a.M20 * b.M01 + a.M21 * b.M11 + a.M22 * b.M21,
                M22 = a.M20 * b.M02 + a.M21 * b.M12 + a.M22 * b.M22
            };

            public Matrix3 Transpose() => new Matrix3
            {
                M00 = M00, M01 = M10, M02 = M20,
                M10 = M01, M11 = M11, M12 = M21,
                M20 = M02, M21 = M12, M22 = M22
            };

            public float Determinant =>
                M00 * (M11 * M22 - M12 * M21) - M01 * (M10 * M22 - M12 * M20) + M02 * (M10 * M21 - M11 * M20);

            // inverse(M)^T = cofactor(M) / det(M).
            public Matrix3 InverseTranspose()
            {
                float det = Determinant;
                if (Mathf.Abs(det) < 1e-20f)
                {
                    return Identity;
                }

                float inv = 1f / det;
                return new Matrix3
                {
                    M00 = (M11 * M22 - M12 * M21) * inv,
                    M01 = -(M10 * M22 - M12 * M20) * inv,
                    M02 = (M10 * M21 - M11 * M20) * inv,
                    M10 = -(M01 * M22 - M02 * M21) * inv,
                    M11 = (M00 * M22 - M02 * M20) * inv,
                    M12 = -(M00 * M21 - M01 * M20) * inv,
                    M20 = (M01 * M12 - M02 * M11) * inv,
                    M21 = -(M00 * M12 - M02 * M10) * inv,
                    M22 = (M00 * M11 - M01 * M10) * inv
                };
            }

            public static Matrix3 Identity => new Matrix3 { M00 = 1f, M11 = 1f, M22 = 1f };

            public bool IsIdentity(float tolerance) =>
                Mathf.Abs(M00 - 1f) <= tolerance && Mathf.Abs(M11 - 1f) <= tolerance && Mathf.Abs(M22 - 1f) <= tolerance &&
                Mathf.Abs(M01) <= tolerance && Mathf.Abs(M02) <= tolerance && Mathf.Abs(M10) <= tolerance &&
                Mathf.Abs(M12) <= tolerance && Mathf.Abs(M20) <= tolerance && Mathf.Abs(M21) <= tolerance;

            // The rotation R of M = R * P with P symmetric (polar decomposition), the rotation
            // GfMatrix4d::Factor leaves once scale and shear are taken out. Higham's iteration:
            // average with the inverse transpose until it stops changing.
            public Matrix3 PolarRotation()
            {
                Matrix3 r = this;
                if (r.Determinant < 0f)
                {
                    r = r * new Matrix3 { M00 = -1f, M11 = -1f, M22 = -1f };
                }

                for (int i = 0; i < 32; i++)
                {
                    Matrix3 inverseTranspose = r.InverseTranspose();
                    var next = new Matrix3
                    {
                        M00 = 0.5f * (r.M00 + inverseTranspose.M00), M01 = 0.5f * (r.M01 + inverseTranspose.M01),
                        M02 = 0.5f * (r.M02 + inverseTranspose.M02), M10 = 0.5f * (r.M10 + inverseTranspose.M10),
                        M11 = 0.5f * (r.M11 + inverseTranspose.M11), M12 = 0.5f * (r.M12 + inverseTranspose.M12),
                        M20 = 0.5f * (r.M20 + inverseTranspose.M20), M21 = 0.5f * (r.M21 + inverseTranspose.M21),
                        M22 = 0.5f * (r.M22 + inverseTranspose.M22)
                    };

                    float change = Mathf.Abs(next.M00 - r.M00) + Mathf.Abs(next.M01 - r.M01) + Mathf.Abs(next.M02 - r.M02) +
                                   Mathf.Abs(next.M10 - r.M10) + Mathf.Abs(next.M11 - r.M11) + Mathf.Abs(next.M12 - r.M12) +
                                   Mathf.Abs(next.M20 - r.M20) + Mathf.Abs(next.M21 - r.M21) + Mathf.Abs(next.M22 - r.M22);
                    r = next;
                    if (change < 1e-7f)
                    {
                        break;
                    }
                }

                return r;
            }

            public Quaternion ToQuaternion()
            {
                var m = new Matrix4x4(
                    new Vector4(M00, M10, M20, 0f),
                    new Vector4(M01, M11, M21, 0f),
                    new Vector4(M02, M12, M22, 0f),
                    new Vector4(0f, 0f, 0f, 1f));
                return m.rotation;
            }
        }
    }
}
